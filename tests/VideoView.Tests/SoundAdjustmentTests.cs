using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VideoView.Core;
using VideoView.Core.Media;
using VideoView.Platform.Audio;

namespace VideoView.Tests;

internal static class SoundAdjustmentTests
{
    public static async Task RunAsync(FfmpegTools tools, string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        TestVolumePolicy(assert);
        var source = Path.Combine(directory, "quiet-then-loud.mkv");
        // File generators only: no output devices, microphones, or real recordings are opened.
        await tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-n",
            "-f", "lavfi", "-i", "color=c=navy:s=320x180:r=15:d=12",
            "-f", "lavfi", "-i", "aevalsrc=0.008*sin(2*PI*440*t)*if(lt(t\\,6)\\,1\\,20):s=48000:d=12",
            "-f", "lavfi", "-i", "aevalsrc=0.002*sin(2*PI*880*t):s=48000:d=12",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=12",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", "-pix_fmt", "yuv420p", "-c:a", "pcm_f32le", source]);
        var hash = await Hash(source);
        var recording = new RecordingManifest { MediaPath = source, HasVideo = true, DurationSeconds = 12 };
        var service = new ExportService(tools);
        var request = new ExportRequest(recording, .5, 4.5, true, 1,
            [new(0, 1), new(1, 1, false)], Path.Combine(directory, "normalized.m4a"), true);
        var progress = new List<double>();
        var normalized = await service.ExportDetailedAsync(request, progress: new ImmediateProgress(progress.Add));
        var levels = await Levels(tools, normalized.Path);
        assert(levels.Lufs is > -16.7 and < -15.3 && levels.Peak < -1,
            "Quiet selected audio reaches approximately -16 LUFS with peak headroom");
        assert(normalized.Normalization is { Measurable: true, GainDecibels: > 20 and < 36, Limited: false },
            "Loudness analysis excludes the louder audio outside the selected trim");
        var samples = await Decode(tools, normalized.Path, directory, "selected");
        assert(Tone(samples, 440) > .1 && Tone(samples, 880) < .001,
            "Normalization does not restore an excluded audio track");
        assert(progress[0] == 0 && progress[^1] == 1 && progress.Any(p => p is > 0 and <= .15) &&
            progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second),
            "Analysis and encoding share monotonic, cancellable progress");

        var off = await service.ExportAsync(request with { OutputPath = Path.Combine(directory, "off.m4a"), NormalizeAudio = false });
        var offSamples = await Decode(tools, off, directory, "off");
        assert(Tone(offSamples, 440) < .009 && Tone(samples, 440) > Tone(offSamples, 440) * 10,
            "Opting out preserves the original quiet level; opting in materially boosts it");

        var mixed = await service.ExportDetailedAsync(request with
        { Tracks = [new(0, 1), new(1, .5)], OutputPath = Path.Combine(directory, "mixed.mp4"), AudioOnly = false });
        var mixedSamples = await Decode(tools, mixed.Path, directory, "mixed");
        var ratio = Tone(mixedSamples, 880) / Tone(mixedSamples, 440);
        var videoInfo = await tools.ProbeAsync(mixed.Path);
        assert(ratio is > .1 and < .15, "Normalized audio preserves the user's inter-track volume balance");
        assert(videoInfo.HasVideo && videoInfo.Tracks.Count == 1 && Math.Abs(videoInfo.DurationSeconds - 4) < .1 &&
            new FileInfo(mixed.Path).Length <= 1_000_000, "Normalized MP4 retains trim, one mixed track, and size ceiling");

        foreach (var (name, tracks) in new (string, ExportTrack[])[]
        { ("zero", [new(0, 0)]), ("silence", [new(2, 1)]) })
        {
            var silent = await service.ExportDetailedAsync(request with { Tracks = tracks, OutputPath = Path.Combine(directory, name + ".m4a") });
            var silentSamples = await Decode(tools, silent.Path, directory, name);
            assert(silent.Normalization is { Measurable: false, GainDecibels: 0 } && silentSamples.All(s => float.IsFinite(s) && Math.Abs(s) < .00001),
                $"{name}: silent audio remains silent and reports skipped normalization");
        }
        var capped = await service.ExportDetailedAsync(request with
        { Tracks = [new(0, .15)], OutputPath = Path.Combine(directory, "capped.m4a") });
        assert(capped.Normalization is { Limited: true, GainDecibels: 36 }, "Extremely quiet measurable audio respects the +36 dB boost ceiling");
        var loud = await service.ExportDetailedAsync(request with
        { StartSeconds = 7, EndSeconds = 11, Tracks = [new(0, 5)], OutputPath = Path.Combine(directory, "loud.m4a") });
        var loudLevels = await Levels(tools, loud.Path);
        assert(loud.Normalization is { GainDecibels: < 0 } && loudLevels.Lufs is > -16.7 and < -15.3 && loudLevels.Peak < -1,
            "Already-loud mixes are reduced rather than amplified or clipped");
        var transientSource = Path.Combine(directory, "transients.wav");
        await tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-n", "-f", "lavfi", "-i",
            "aevalsrc=if(lt(mod(t\\,1)\\,0.002)\\,0.8\\,0.006*sin(2*PI*440*t)):s=48000:d=4", "-c:a", "pcm_f32le", transientSource]);
        var transientRecording = new RecordingManifest { MediaPath = transientSource, DurationSeconds = 4 };
        var transient = await service.ExportDetailedAsync(new(transientRecording, 0, 4, true, 1, [new(0, 1)],
            Path.Combine(directory, "transients.m4a"), true));
        var transientLevels = await Levels(tools, transient.Path);
        assert(transient.Normalization is { Limited: true } && transientLevels.Peak < -.5 && transientLevels.Lufs < -17,
            "Transient peaks constrain amplification instead of clipping to force a loudness target");
        var shortClip = await service.ExportDetailedAsync(request with
        { EndSeconds = .6, OutputPath = Path.Combine(directory, "short.m4a") });
        assert(shortClip.Normalization is { Measurable: false } && (await tools.ProbeAsync(shortClip.Path)).DurationSeconds < .2,
            "Sub-gate short clips remain exportable without invalid loudness gains");
        var noAudio = await service.ExportDetailedAsync(request with
        { Tracks = [], AudioOnly = false, OutputPath = Path.Combine(directory, "no-audio.mp4") });
        assert(noAudio.Normalization == null && (await tools.ProbeAsync(noAudio.Path)).Tracks.Count == 0,
            "Video without selected audio skips analysis and stays audio-free");

        using var cancellation = new CancellationTokenSource();
        var canceled = Path.Combine(directory, "canceled.m4a");
        var wasCanceled = false;
        try
        {
            await service.ExportAsync(request with { OutputPath = canceled }, new ImmediateProgress(value =>
            { if (value is > 0 and <= .15) cancellation.Cancel(); }), cancellation.Token);
        }
        catch (OperationCanceledException) { wasCanceled = true; }
        assert(wasCanceled && !File.Exists(canceled), "Canceling analysis publishes no file");
        assert(!Directory.EnumerateFiles(directory).Any(path => path.Contains(".partial") || path.Contains(".passlog")),
            "Normalization exports and cancellation clean all owned temporary files");
        var finalHash = await Hash(source);
        assert(hash.SequenceEqual(finalHash), "All audio correction preserves original recording bytes");
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        assert(defaults.PreviewVolume == .1, "Older settings get a quiet 10% preview default");
        defaults.PreviewVolume = double.NaN;
        var rejected = false; try { defaults.Validate(); } catch (ArgumentException) { rejected = true; }
        assert(rejected, "Non-finite preview volume cannot be persisted");
    }

    private static void TestVolumePolicy(Action<bool, string> assert)
    {
        var device = new OutputVolumeSnapshot("headphones", "Synthetic headphones", -20, .26f, false, -96, 0, .5f);
        assert(ListeningVolumeService.ReducedTarget(device, device, 6) == -26, "Explicit output attenuation lowers master dB");
        var muted = device with { Muted = true };
        assert(ListeningVolumeService.ReducedTarget(muted, muted, 6) == -26 && muted.Muted,
            "Output reduction policy preserves mute");
        var low = device with { Decibels = -95 };
        assert(ListeningVolumeService.ReducedTarget(low, low, 20) == -96, "Device minimum clamps large attenuation requests");
        var minimum = device with { Decibels = -96 };
        var atMinimumBlocked = false;
        try { ListeningVolumeService.ReducedTarget(minimum, minimum, 6); }
        catch (InvalidOperationException) { atMinimumBlocked = true; }
        assert(atMinimumBlocked, "An output already at its minimum cannot claim a successful further reduction");
        var odd = device with { Decibels = -20.1f };
        assert(ListeningVolumeService.ReducedTarget(odd, odd, 3) <= -23.1, "Hardware step rounding never increases requested output");
        foreach (var changed in new[] { device with { Id = "another" }, device with { Decibels = -45 },
                     device with { Muted = true }, device with { Decibels = -96 }, device with { IncrementDecibels = float.NaN } })
        {
            var blocked = false;
            try { ListeningVolumeService.ReducedTarget(device, changed, 6); }
            catch (InvalidOperationException) { blocked = true; }
            assert(blocked, "Changed endpoint, changed master level, or unsupported range requires a fresh read");
        }
        foreach (var invalid in new[] { 0d, -6d, double.NaN, double.PositiveInfinity, 61d })
        {
            var blocked = false;
            try { ListeningVolumeService.ReducedTarget(device, device, invalid); }
            catch (ArgumentOutOfRangeException) { blocked = true; }
            assert(blocked, "Output control rejects increases, zero, and invalid attenuation");
        }
    }

    private static async Task<(double Lufs, double Peak)> Levels(FfmpegTools tools, string path)
    {
        var text = await tools.AnalyzeAsync(["-hide_banner", "-nostdin", "-v", "info", "-i", path, "-vn",
            "-af", "loudnorm=I=-16:TP=-2:print_format=json", "-f", "null", "-"]);
        using var json = JsonDocument.Parse(text[text.LastIndexOf('{')..(text.LastIndexOf('}') + 1)]);
        return (double.Parse(json.RootElement.GetProperty("input_i").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(json.RootElement.GetProperty("input_tp").GetString()!, CultureInfo.InvariantCulture));
    }
    private static async Task<float[]> Decode(FfmpegTools tools, string path, string directory, string name)
    {
        var raw = Path.Combine(directory, name + ".f32");
        await tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-n", "-ss", "0.2", "-i", path,
            "-t", "1", "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "f32le", raw]);
        var bytes = await File.ReadAllBytesAsync(raw); var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length); return samples;
    }
    private static double Tone(float[] samples, int frequency)
    {
        double real = 0, imaginary = 0;
        for (var i = 0; i < samples.Length; i++)
        { var angle = 2 * Math.PI * frequency * i / 48000; real += samples[i] * Math.Cos(angle); imaginary += samples[i] * Math.Sin(angle); }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }
    private static async Task<byte[]> Hash(string path)
    { await using var file = File.OpenRead(path); return await SHA256.HashDataAsync(file); }
    private sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }
}
