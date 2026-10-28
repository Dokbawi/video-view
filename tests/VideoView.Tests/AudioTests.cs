using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VideoView.Platform.Audio;

namespace VideoView.Tests;

public static class AudioTests
{
    /// <summary>Deterministic checks; never starts an audio device or recording.</summary>
    public static void Run(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        var descriptor = new AudioSourceDescriptor("system", "System", AudioSourceKind.System);
        assert(descriptor.ProcessId is null, "Audio descriptor ProcessId defaults to null");
        using (var source = AudioSourceFactory.Create(descriptor))
        {
            var output = Enumerable.Repeat(9f, 964).ToArray();
            assert(source.Name == "System" && source.Peak == 0, "New audio source is idle");
            assert(source.Read(output, 2, 960) == 960 && output.Skip(2).Take(960).All(value => value == 0),
                "Idle source fills a complete 10 ms block with silence");
            assert(output[0] == 9 && output[1] == 9 && output[^1] == 9, "Read respects requested bounds");
            source.Stop();
            source.Stop();
            source.Dispose();
            assert(source.Read(output, 0, output.Length) == output.Length && output.All(value => value == 0),
                "Disposed audio source remains safe for a racing reader");
            assert(Throws<ObjectDisposedException>(source.Start), "Disposed source cannot restart");
            assert(Throws<ArgumentException>(() => source.Read(output, output.Length, 1)), "Invalid read range rejected");
            assert(Throws<ArgumentOutOfRangeException>(() => source.Read(output, -1, 1)), "Negative read offset rejected");
        }
        assert(Throws<ArgumentException>(() => AudioSourceFactory.Create(new("app", "App", AudioSourceKind.Application))),
            "Application capture requires a PID and cannot silently fall back to system capture");
        assert(Throws<ArgumentException>(() => AudioSourceFactory.Create(new("app", "App", AudioSourceKind.Application, 0))),
            "Application PID zero rejected");
        assert(Throws<ArgumentOutOfRangeException>(() => AudioSourceFactory.Create(new("x", "X", (AudioSourceKind)99))),
            "Unknown source kind rejected");

        var time = new ManualTime();
        var queue = new AudioSampleBuffer(time);
        var chunk = StereoPacket(480, .25f, -.5f);
        var samples = new float[960];
        queue.Reset(true);
        queue.Write(chunk, chunk.Length, false);
        assert(queue.Read(samples, 0, samples.Length) == 960 && samples.SequenceEqual(chunk), "10 ms stereo packet round trip");
        queue.Read(samples, 0, samples.Length);
        assert(samples.All(value => value == 0), "Underflow zero-fills every requested sample");
        queue.Write(chunk, 2, false);
        queue.Read(samples, 0, samples.Length);
        assert(samples[0] == .25f && samples[1] == -.5f && samples.Skip(2).All(value => value == 0),
            "Partial underflow preserves available audio then pads silence");

        queue.Reset(true);
        queue.Read(samples, 0, 1);
        queue.Write(chunk, 2, false);
        queue.Read(samples, 0, 1);
        assert(samples[0] == -.5f, "Odd underflow keeps left/right phase");
        queue.Reset(true);
        queue.Write(chunk, 2, false);
        queue.Read(samples, 0, 1);
        var capacity = AudioSampleBuffer.SampleRate * AudioSampleBuffer.Channels * AudioSampleBuffer.BacklogMilliseconds / 1_000;
        var recovery = AudioSampleBuffer.SampleRate * AudioSampleBuffer.Channels * AudioSampleBuffer.RecoveryMilliseconds / 1_000;
        var full = StereoPacket(capacity / 2, .25f, -.5f);
        queue.Write(full, full.Length, false);
        queue.Read(samples, 0, 1);
        assert(samples[0] == -.5f && queue.BufferedSamples <= recovery, "Odd overflow preserves channels and recovers low latency");

        queue.Reset(true);
        var oversized = Enumerable.Range(0, capacity * 3).Select(index => (float)index).ToArray();
        queue.Write(oversized, oversized.Length, false);
        queue.Read(samples, 0, 2);
        assert(samples[0] == oversized[^recovery] && samples[1] == oversized[^(recovery - 1)],
            "Oversized packet keeps newest 20 ms, discards old audio");
        queue.Reset(true);
        queue.Write(full, full.Length, false);
        queue.Write(chunk, chunk.Length, false);
        assert(queue.BufferedSamples == recovery, "Queue overrun returns to 20 ms instead of retaining maximum delay");
        time.Advance(TimeSpan.FromMilliseconds(41));
        queue.Read(samples, 0, samples.Length);
        assert(samples.All(value => value == 0) && queue.BufferedSamples == 0, "Stale samples never replay after a consumer pause");
        queue.Write(chunk, chunk.Length, false);
        time.Advance(TimeSpan.FromMilliseconds(151));
        assert(queue.Peak == 0, "Peak decays to zero without further capture callbacks");
        queue.Reset(false);
        queue.Write(chunk, chunk.Length, false);
        assert(queue.BufferedSamples == 0 && queue.Peak == 0, "Late capture callbacks cannot refill a stopped source");

        CheckClockDrift(time, queue, assert);
        CheckNonblockingRead(queue, assert);
        CheckNativeLayout(assert);
    }

    private static void CheckClockDrift(ManualTime time, AudioSampleBuffer queue, Action<bool, string> assert)
    {
        queue.Reset(true);
        var output = new float[960];
        var withinBudget = true;
        var allReadsFilled = true;
        // Model 60 seconds of a 10 ms master clock with delayed producer batches and
        // recurring consumer underruns. The simulation uses virtual time, not sleeps.
        for (var tick = 1; tick <= 6_000; tick++)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            if (tick % 17 != 0)
            {
                var packet = StereoPacket(tick % 17 == 1 ? 960 : 480, tick, -tick);
                queue.Write(packet, packet.Length, false);
            }
            var reads = tick % 31 == 0 ? 2 : 1;
            for (var read = 0; read < reads; read++)
            {
                allReadsFilled &= queue.Read(output, 0, output.Length) == output.Length;
                for (var index = 0; index < output.Length; index += 2)
                {
                    if (output[index] == 0)
                        continue;
                    withinBudget &= tick - output[index] <= 4 && output[index + 1] == -output[index];
                }
            }
            withinBudget &= queue.BufferedSamples <= 3_840;
        }
        assert(allReadsFilled, "Master clock advances exactly 480 frames per read, including underruns");
        assert(withinBudget, "Repeated underruns and delayed packets do not accumulate queue drift beyond 40 ms");
    }

    private static void CheckNonblockingRead(AudioSampleBuffer queue, Action<bool, string> assert)
    {
        queue.Reset(true);
        var old = StereoPacket(480, .25f, -.5f);
        queue.Write(old, old.Length, false);
        var gate = typeof(AudioSampleBuffer).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } }) { IsBackground = true };
        holder.Start();
        held.Wait();
        var result = new float[960];
        var reader = Task.Run(() => queue.Read(result, 0, result.Length));
        try
        {
            assert(reader.Wait(TimeSpan.FromMilliseconds(250)), "Read completes while the producer gate is held");
        }
        finally
        {
            release.Set();
            holder.Join();
            reader.GetAwaiter().GetResult();
        }
        assert(result.All(value => value == 0), "Contended read supplies silence instead of waiting");
        var fresh = StereoPacket(480, .75f, -.75f);
        queue.Write(fresh, fresh.Length, false);
        queue.Read(result, 0, result.Length);
        assert(result.SequenceEqual(fresh), "Contended interval discarded; old samples cannot become permanent delay");
    }

    private static void CheckNativeLayout(Action<bool, string> assert)
    {
        var activation = typeof(AudioSourceFactory).Assembly.GetType("VideoView.Platform.Audio.ProcessLoopbackActivation")!;
        var variant = activation.GetNestedType("BlobVariant", BindingFlags.NonPublic)!;
        var parameters = activation.GetNestedType("ActivationParameters", BindingFlags.NonPublic)!;
        assert(Marshal.SizeOf(variant) == (IntPtr.Size == 8 ? 24 : 16) &&
               Marshal.OffsetOf(variant, "BlobData").ToInt32() == (IntPtr.Size == 8 ? 16 : 12),
            "Process activation PROPVARIANT follows native pointer alignment");
        assert(Marshal.SizeOf(parameters) == 12, "Process activation parameters have the native 12-byte layout");
        var callback = Activator.CreateInstance(activation.GetNestedType("CompletionHandler", BindingFlags.NonPublic)!, true)!;
        var unknown = Marshal.GetIUnknownForObject(callback);
        try
        {
            foreach (var id in new[] { "94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90", "41D949AB-9862-444A-80F6-C261334DA5EB" })
            {
                var guid = new Guid(id);
                var result = Marshal.QueryInterface(unknown, in guid, out var pointer);
                try { assert(result == 0, "Process activation callback exposes COM interface " + id); }
                finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
            }
        }
        finally { Marshal.Release(unknown); }
    }

    /// <summary>
    /// Opt-in native check. The test executable must handle --tone by playing a generated
    /// stereo tone for at least five seconds, without running any other tests. No microphone
    /// or system source is opened, and captured PCM is never written to disk.
    /// </summary>
    public static async Task NativeSmokeAsync(string dir, Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No test host executable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--tone");
        using var tone = Process.Start(start) ?? throw new InvalidOperationException("Could not start generated tone process.");
        var stdout = tone.StandardOutput.ReadToEndAsync();
        var stderr = tone.StandardError.ReadToEndAsync();
        var faults = new ConcurrentQueue<string>();
        using var source = AudioSourceFactory.Create(new("process:" + tone.Id, "Generated test tone", AudioSourceKind.Application, tone.Id));
        source.Faulted += faults.Enqueue;
        var data = new float[960];
        var signalBlocks = 0;
        var finite = true;
        var fullReads = true;
        var maxReadMs = 0d;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            source.Start();
            for (var block = 0; block < 600 && faults.IsEmpty; block++)
            {
                var delay = block * .01 - stopwatch.Elapsed.TotalSeconds;
                if (delay > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                var before = Stopwatch.GetTimestamp();
                fullReads &= source.Read(data, 0, data.Length) == data.Length;
                maxReadMs = Math.Max(maxReadMs, Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                if (data.Any(value => Math.Abs(value) > .001f))
                    signalBlocks++;
                finite &= data.All(value => float.IsFinite(value) && value is >= -1 and <= 1);
            }
            source.Stop();
            var stoppedTone = !tone.HasExited;
            if (stoppedTone)
                tone.Kill(entireProcessTree: true); // Only the helper created by this method.
            await tone.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var diagnostics = await stderr;
            _ = await stdout;
            assert(faults.IsEmpty, "Native process loopback faults: " + string.Join("; ", faults));
            assert(stoppedTone || tone.ExitCode == 0, "Generated tone process completes or is stopped by its owner: " + diagnostics);
            assert(signalBlocks >= 20, "Real process-tree loopback captures at least 200 ms of generated tone");
            assert(finite && fullReads, "Native capture returns complete normalized float stereo blocks");
            assert(maxReadMs < 100, "Native Read stays within the 100 ms master-clock budget");
            Array.Fill(data, 1);
            source.Read(data, 0, data.Length);
            assert(data.All(value => value == 0) && source.Peak == 0, "Native Stop clears samples and peak");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "audio-smoke.txt"),
                $"Generated tone process only. Signal blocks: {signalBlocks}; maximum Read: {maxReadMs:F3} ms. No captured audio saved.");
        }
        finally
        {
            source.Stop();
            if (!tone.HasExited)
            {
                tone.Kill(entireProcessTree: true);
                await tone.WaitForExitAsync();
            }
        }
    }

    private static float[] StereoPacket(int frames, float left, float right)
    {
        var samples = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = left;
            samples[frame * 2 + 1] = right;
        }
        return samples;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp = TimeSpan.TicksPerSecond;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
