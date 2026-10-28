using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VideoView.Core;
using VideoView.Core.Media;

namespace VideoView.Tests;

internal static class PreviewCacheTests
{
    public static async Task RunAsync(FfmpegTools tools, RecordingManifest seed, string root, Action<bool, string> assert)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "long-source.mkv");
        await tools.RunAsync(["-v", "error", "-stream_loop", "7", "-i", seed.MediaPath, "-t", "64", "-map", "0", "-c", "copy", path]);
        var info = await tools.ProbeAsync(path);
        var source = new RecordingManifest { MediaPath = path, DurationSeconds = info.DurationSeconds, HasVideo = true, Tracks = info.Tracks.ToList() };
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var cacheRoot = Path.Combine(root, "cache");
        var cache = new PreviewCache(tools, cacheRoot);
        ExportTrack[] mix = [new(0, 1), new(1, 0.5)];
        string firstPath;
        using (var slice = await cache.GetSliceAsync(source, mix, 25.37))
        {
            firstPath = slice.Path;
            var sliceInfo = await tools.ProbeAsync(slice.Path);
            assert(slice.Start == 20 && slice.End == 40 && Math.Abs(sliceInfo.DurationSeconds - 20) < .1 &&
                sliceInfo.HasVideo && sliceInfo.Tracks.Count == 1, "Preview decodes only the requested 20-second window with one mixed audio track");
            assert(sliceInfo.Width == 480 && sliceInfo.Height == 270 && Math.Abs(sliceInfo.FramesPerSecond - 30) < .01,
                "Fast preview does not upscale small sources or change their frame rate");
            var expected = Path.Combine(root, "expected.rgb"); var actual = Path.Combine(root, "actual.rgb");
            await tools.RunAsync(["-v", "error", "-ss", "20", "-i", path, "-frames:v", "1", "-vf", "scale=32:18", "-pix_fmt", "rgb24", "-f", "rawvideo", expected]);
            await tools.RunAsync(["-v", "error", "-i", slice.Path, "-frames:v", "1", "-vf", "scale=32:18", "-pix_fmt", "rgb24", "-f", "rawvideo", actual]);
            var a = await File.ReadAllBytesAsync(expected); var b = await File.ReadAllBytesAsync(actual);
            assert(a.Length == b.Length && a.Zip(b).Average(p => Math.Abs(p.First - p.Second)) < 12,
                "Preview window begins at the requested source frame rather than its preceding keyframe");
            using var hit = await new PreviewCache(new FfmpegTools(Path.Combine(root, "no-encoder.exe"), tools.FfprobePath), cacheRoot)
                .GetSliceAsync(source, mix, 28);
            assert(hit.CacheHit && hit.Path == slice.Path, "Reopening a cached range needs no encoder, including across cache instances");
            using var changed = await cache.GetSliceAsync(source, [new(0, 0), new(1, 1, false)], 25);
            assert(!changed.CacheHit && changed.Path != slice.Path, "Gain and excluded-track changes invalidate only the relevant mixed preview");
            var raw = Path.Combine(root, "muted-preview.raw");
            await tools.RunAsync(["-v", "error", "-i", changed.Path, "-map", "0:a:0", "-ac", "1", "-ar", "8000", "-f", "f32le", raw]);
            var samples = await File.ReadAllBytesAsync(raw);
            var silent = true;
            for (var i = 0; i + 4 <= samples.Length; i += 4)
                if (Math.Abs(BitConverter.ToSingle(samples, i)) > 0.0001f) { silent = false; break; }
            assert(silent && samples.Length > 0, "Preview audio respects zero gain and track exclusion after a cache-key change");
        }
        await File.WriteAllTextAsync(firstPath, "broken preview");
        using (var repaired = await cache.GetSliceAsync(source, mix, 25))
            assert(!repaired.CacheHit && (await tools.ProbeAsync(repaired.Path)).HasVideo, "Corrupt preview cache regenerates without touching the source");
        var waves = await cache.GetWaveformsAsync(source);
        var cachedWaves = await new PreviewCache(new FfmpegTools(Path.Combine(root, "no-encoder.exe"), tools.FfprobePath), cacheRoot).GetWaveformsAsync(source);
        assert(waves.Count == 2 && cachedWaves[0].Peaks.SequenceEqual(waves[0].Peaks), "Waveforms survive reopen without decoding the full source again");
        await File.WriteAllTextAsync(Directory.EnumerateFiles(cacheRoot, "*.wave.json").Single(), "[null,null]");
        assert((await cache.GetWaveformsAsync(source)).Count == 2, "Malformed waveform cache regenerates without blocking preview playback");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        using (var changed = await cache.GetSliceAsync(source, mix, 25))
            assert(!changed.CacheHit && changed.Path != firstPath, "A changed source timestamp invalidates stale previews");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var canceled = false;
            try { using var ignored = await cache.GetSliceAsync(source, mix, 45, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            assert(canceled && !Directory.EnumerateFiles(cacheRoot).Any(p => p.EndsWith(".partial")), "Canceled preview work never publishes a partial cache entry");
        }
        var tinyRoot = Path.Combine(root, "tiny-cache"); var tiny = new PreviewCache(tools, tinyRoot, 1);
        var unrelated = Path.Combine(tinyRoot, "original-do-not-delete.mkv"); File.WriteAllText(unrelated, "keep");
        var pinned = await tiny.GetSliceAsync(source, mix, 0);
        assert(File.Exists(pinned.Path), "Cache eviction keeps the active player file pinned even above its soft limit");
        pinned.Dispose();
        assert(!File.Exists(pinned.Path) && File.Exists(unrelated), "Released preview files are evicted without deleting unrelated files");
        assert(SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(originalHash), "Preview, cache repair and eviction preserve original media bytes");
    }

    public static async Task BenchmarkAsync(FfmpegTools tools, string root)
    {
        Directory.CreateDirectory(root);
        var seed = Path.Combine(root, "seed.mkv"); var path = Path.Combine(root, "one-hour.mkv");
        await tools.RunAsync(["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=480x270:rate=30:duration=8",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=8", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=8",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-c:a", "aac", "-b:a", "96k", seed]);
        await tools.RunAsync(["-v", "error", "-stream_loop", "449", "-i", seed, "-t", "3600", "-map", "0", "-c", "copy", path]);
        var info = await tools.ProbeAsync(path);
        var source = new RecordingManifest { MediaPath = path, DurationSeconds = info.DurationSeconds, HasVideo = true, Tracks = info.Tracks.ToList() };
        ExportTrack[] mix = [new(0, 1), new(1, 1)];
        var timer = Stopwatch.StartNew();
        await new PreviewService(tools).CreateAsync(source, mix, Path.Combine(root, "old-whole-file.mp4"));
        var whole = timer.Elapsed.TotalSeconds;
        var cache = new PreviewCache(tools, Path.Combine(root, "cache"));
        timer.Restart(); using var first = await cache.GetSliceAsync(source, mix, 2405);
        var window = timer.Elapsed.TotalSeconds;
        timer.Restart(); using var again = await cache.GetSliceAsync(source, mix, 2405);
        var cached = timer.Elapsed.TotalSeconds;
        var result = new { SourceDuration = source.DurationSeconds, Width = info.Width, Height = info.Height,
            WholeFileSeconds = whole, FirstWindowSeconds = window, CachedWindowSeconds = cached, again.CacheHit };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(root, "benchmark.json"), json);
        Console.WriteLine(json);
    }
}
