using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VideoView.Platform.Audio;

internal sealed class WasapiAudioSource : IAudioSource
{
    private readonly object _gate = new();
    private readonly AudioSourceDescriptor _descriptor;
    private readonly AudioSampleBuffer _buffer = new();
    private CaptureRun? _run;
    private bool _disposed;

    public WasapiAudioSource(AudioSourceDescriptor descriptor) => _descriptor = descriptor;
    public string Name => _descriptor.Name;
    public event Action<string>? Faulted;
    public float Peak => _buffer.Peak;
    public int Read(float[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("WASAPI audio capture requires Windows.");
            if (_run is not null)
            {
                if (_run.Cancellation.IsCancellationRequested)
                    throw new InvalidOperationException("The previous audio capture is still stopping.");
                return;
            }

            var run = new CaptureRun();
            run.Thread = new Thread(() => Capture(run))
            {
                IsBackground = true,
                Name = "VideoView audio: " + Name
            };
            run.Thread.SetApartmentState(ApartmentState.MTA);
            _buffer.Reset(accepting: true);
            _run = run;
            try { run.Thread.Start(); }
            catch
            {
                _run = null;
                _buffer.Reset(accepting: false);
                run.Cancellation.Dispose();
                throw;
            }
        }
    }

    public void Stop() => StopCore(disposing: false);

    public void Dispose()
    {
        StopCore(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void StopCore(bool disposing)
    {
        CaptureRun? run;
        lock (_gate)
        {
            if (disposing)
                _disposed = true;
            run = _run;
            run?.Cancellation.Cancel();
            _buffer.Reset(accepting: false);
        }

        // Never join while holding _gate; the worker needs it for cleanup. Native driver
        // calls can hang, so retain ownership on the worker and bound caller wait time.
        if (run is not null && Thread.CurrentThread != run.Thread && !run.Thread.Join(TimeSpan.FromSeconds(5)))
            ReportFault(run, "Audio capture did not stop within five seconds; its driver may be unresponsive.");
    }

    private void Capture(CaptureRun run)
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? endpoint = null;
        AudioClient? client = null;
        var started = false;
        Exception? failure = null;
        using var audioReady = new AutoResetEvent(false);
        try
        {
            var token = run.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            if (_descriptor.Kind == AudioSourceKind.Application)
            {
                using var process = Process.GetProcessById(_descriptor.ProcessId!.Value);
                if (process.HasExited)
                    throw new InvalidOperationException("The selected application's process has exited.");
                client = ProcessLoopbackActivation.Activate(process.Id, token);
            }
            else
            {
                enumerator = new MMDeviceEnumerator();
                endpoint = _descriptor.Kind == AudioSourceKind.Microphone
                    ? enumerator.GetDevice(_descriptor.Id)
                    : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var requiredFlow = _descriptor.Kind == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;
                if (endpoint.DataFlow != requiredFlow)
                    throw new InvalidOperationException("The selected endpoint has the wrong audio direction.");
                if (endpoint.State != DeviceState.Active)
                    throw new InvalidOperationException("The selected audio device is disconnected or disabled.");
                client = endpoint.AudioClient;
            }

            token.ThrowIfCancellationRequested();
            var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm |
                        AudioClientStreamFlags.SrcDefaultQuality;
            if (_descriptor.Kind != AudioSourceKind.Microphone)
                flags |= AudioClientStreamFlags.Loopback;
            // AUTOCONVERTPCM supplies channel mapping and resampling. Do not call
            // GetMixFormat/IsFormatSupported on a process-loopback client: not implemented.
            client.Initialize(AudioClientShareMode.Shared, flags, 0, 0,
                WaveFormat.CreateIeeeFloatWaveFormat(AudioSampleBuffer.SampleRate, AudioSampleBuffer.Channels), Guid.Empty);
            client.SetEventHandle(audioReady.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            var packet = new float[checked(Math.Max(1, client.BufferSize) * AudioSampleBuffer.Channels)];
            var waits = new WaitHandle[] { token.WaitHandle, audioReady };
            token.ThrowIfCancellationRequested();
            client.Start();
            started = true;
            var lastDeviceCheck = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waits, 100) == 0)
                    break;
                // Poll on timeout too: a disconnected engine may no longer signal events.
                while (!token.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                {
                    var data = capture.GetBuffer(out var frames, out var bufferFlags, out _, out var capturedAt);
                    try
                    {
                        // WASAPI timestamps are QPC values converted to 100-nanosecond units.
                        // A stalled capture thread must not enqueue old native packets as new
                        // audio when it wakes; trim their leading frames to the latency budget.
                        var skipFrames = 0;
                        if (capturedAt > 0 && (bufferFlags & AudioClientBufferFlags.TimestampError) == 0)
                        {
                            var now = Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency);
                            var oldSeconds = (now - capturedAt) / 10_000_000d - AudioSampleBuffer.BacklogMilliseconds / 1_000d;
                            if (oldSeconds > 0)
                                skipFrames = (int)Math.Min(frames, Math.Ceiling(oldSeconds * AudioSampleBuffer.SampleRate));
                        }
                        var sampleCount = checked((frames - skipFrames) * AudioSampleBuffer.Channels);
                        if (sampleCount > packet.Length)
                            Array.Resize(ref packet, sampleCount);
                        if ((bufferFlags & AudioClientBufferFlags.Silent) != 0)
                            Array.Clear(packet, 0, sampleCount);
                        else if (sampleCount > 0)
                        {
                            if (data == IntPtr.Zero)
                                throw new InvalidOperationException("WASAPI returned a null non-silent audio packet.");
                            Marshal.Copy(IntPtr.Add(data, checked(skipFrames * AudioSampleBuffer.Channels * sizeof(float))), packet, 0, sampleCount);
                            for (var index = 0; index < sampleCount; index++)
                                packet[index] = float.IsFinite(packet[index]) ? Math.Clamp(packet[index], -1f, 1f) : 0f;
                        }
                        _buffer.Write(packet, sampleCount,
                            (bufferFlags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }
                if (endpoint is not null && Stopwatch.GetElapsedTime(lastDeviceCheck).TotalMilliseconds >= 250)
                {
                    if (endpoint.State != DeviceState.Active)
                        throw new InvalidOperationException("The selected audio device was disconnected or disabled.");
                    lastDeviceCheck = Stopwatch.GetTimestamp();
                }
                // Do not stop at root-process exit: included descendants may still render.
                // A quiet process tree legitimately yields silence, not a capture failure.
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!run.Cancellation.IsCancellationRequested)
                failure = exception;
        }
        finally
        {
            // Device invalidation can also make Stop/Dispose fail. Always release every
            // resource and keep those errors away from the unhandled-thread exception path.
            if (started && client is not null)
                Cleanup(client.Stop, ref failure, run);
            if (client is not null)
                Cleanup(client.Dispose, ref failure, run);
            if (endpoint is not null)
                Cleanup(endpoint.Dispose, ref failure, run);
            if (enumerator is not null)
                Cleanup(enumerator.Dispose, ref failure, run);
            lock (_gate)
            {
                if (ReferenceEquals(_run, run))
                {
                    _buffer.Reset(accepting: false);
                    _run = null;
                }
                run.Cancellation.Dispose();
            }
            if (failure is not null)
                ReportFault(run, $"{Name}: {failure.Message} (0x{failure.HResult:X8})");
        }
    }

    private static void Cleanup(Action action, ref Exception? failure, CaptureRun run)
    {
        try { action(); }
        catch (Exception exception)
        {
            if (!run.Cancellation.IsCancellationRequested)
                failure ??= exception;
        }
    }

    private void ReportFault(CaptureRun run, string message)
    {
        if (Interlocked.Exchange(ref run.FaultReported, 1) != 0)
            return;
        var handlers = Faulted;
        if (handlers is null)
            return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try { handler(message); }
                catch (Exception exception) { Trace.TraceError("Audio Faulted subscriber failed: {0}", exception); }
            }
        });
    }

    private sealed class CaptureRun
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Thread Thread { get; set; } = null!;
        internal int FaultReported;
    }
}
