using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VideoView.Core;
using VideoView.Core.Library;
using VideoView.Core.Media;

namespace VideoView.Tests;

/// <summary>Real FFmpeg integration checks. No test framework or additional packages required.</summary>
public static class MediaTests
{
    public static async Task RunAsync(FfmpegTools tools, string workDir, Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(assert);
        // Deliberately exercises spaces, Unicode, and ffconcat apostrophe escaping.
        var root = Path.Combine(Path.GetFullPath(workDir), "media checks ' 한글 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await tools.CheckAsync();
        var source = Path.Combine(root, "two tones.mkv");
        await tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-n",
            "-f", "lavfi", "-i", "testsrc2=size=480x270:rate=30:duration=8",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=8",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=8",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast",
            "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le",
            "-metadata:s:a:0", "title=Microphone 440", "-metadata:s:a:1", "title=Application 880", source]);
        var originalHash = await HashAsync(source);
        var info = await tools.ProbeAsync(source);
        assert(info.HasVideo && info.Tracks.Count == 2, "Probe finds video and both independent audio tracks");
        assert(info.Width == 480 && info.Height == 270 && Math.Abs(info.FramesPerSecond - 30) < 0.001,
            "Editor frame stepping uses the recorded frame rate and source dimensions");
        assert(info.Tracks.Select(track => track.Index).SequenceEqual([0, 1]), "Probe audio indices are zero-based ordinals");
        assert(info.Tracks[0].Name == "Microphone 440" && info.Tracks[1].Name == "Application 880", "Probe preserves track titles");
        assert(Math.Abs(info.DurationSeconds - 8) < 0.05, "Probe reads actual source duration");
        var recording = new RecordingManifest
        {
            MediaPath = source, DurationSeconds = 8, HasVideo = true, Tracks = info.Tracks.ToList()
        };
        var service = new ExportService(tools);
        var waveforms = await new WaveformService(tools).ReadAsync(recording, 160);
        assert(waveforms.Count == 2 && waveforms.All(wave => wave.Peaks.Length == 160), "Waveform decoding retains separate tracks in bounded peak arrays");
        assert(waveforms[0].Name == "Microphone 440" && waveforms.All(wave => wave.Peaks.All(peak => peak is > 0.08f and < 0.2f)),
            "Waveform bins contain actual decoded source amplitudes and track names");
        using (var canceledWaveform = new CancellationTokenSource())
        {
            canceledWaveform.Cancel();
            await RejectAsync<OperationCanceledException>(() => new WaveformService(tools).ReadAsync(recording, token: canceledWaveform.Token),
                assert, "Waveform analysis respects cancellation before starting decoders");
        }
        var video = Path.Combine(root, "share.mp4");
        await PreviewCacheTests.RunAsync(tools, recording, Path.Combine(root, "preview-cache"), assert);
        var progress = new List<double>();
        await service.ExportAsync(new ExportRequest(recording, 0.37, 6.62, false, 1,
            [new ExportTrack(0, 1), new ExportTrack(1, 0.5)], video), new InlineProgress(progress.Add));
        var videoInfo = await tools.ProbeAsync(video);
        assert(videoInfo.HasVideo && videoInfo.Tracks.Count == 1, "Video sharing export contains one mixed audio stream");
        assert(Math.Abs(videoInfo.DurationSeconds - 6.25) < 0.10, "Non-keyframe sharing trim has requested duration");
        assert(new FileInfo(video).Length is > 0 and <= 1_000_000, "Two-pass export respects decimal MB ceiling");
        assert(progress.Count > 2 && progress[0] == 0 && progress[^1] == 1 &&
            progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second), "Export progress is monotonic and completes at one");
        using (var metadata = await ProbeJsonAsync(tools, video))
        {
            var streams = metadata.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            assert(streams.Any(stream => stream.GetProperty("codec_name").GetString() == "h264"), "MP4 video codec is H.264");
            assert(streams.Any(stream => stream.GetProperty("codec_name").GetString() == "aac" && stream.GetProperty("channels").GetInt32() == 2),
                "Default export uses stereo AAC");
        }

        var selection = Path.Combine(root, "selected.m4a");
        var selectedRequest = new ExportRequest(recording, 1.13, 3.38, true, 1,
            [new ExportTrack(0, 1, false), new ExportTrack(1, 0.5)], selection);
        await service.ExportAsync(selectedRequest, audioChannels: 1);
        var selectedInfo = await tools.ProbeAsync(selection);
        assert(!selectedInfo.HasVideo && selectedInfo.Tracks.Count == 1 && Math.Abs(selectedInfo.DurationSeconds - 2.25) < 0.05,
            "M4A selection has accurate duration and one audio track");
        using (var metadata = await ProbeJsonAsync(tools, selection))
            assert(metadata.RootElement.GetProperty("streams")[0].GetProperty("channels").GetInt32() == 1, "Mono export uses one channel");
        var selectedSamples = await DecodeAudioAsync(tools, selection, 0, Path.Combine(root, "selected.raw"));
        var sourceSamples = await DecodeAudioAsync(tools, source, 1, Path.Combine(root, "source.raw"));
        var sourceAmplitude = ToneAmplitude(sourceSamples, 880);
        var ratio = ToneAmplitude(selectedSamples, 880) / sourceAmplitude;
        assert(ratio is > 0.43 and < 0.57, "Selected track gain is preserved at 50 percent");
        assert(ToneAmplitude(selectedSamples, 440) < 0.003, "Excluded track contributes no 440 Hz tone");

        var mix = Path.Combine(root, "mix.m4a");
        await service.ExportAsync(selectedRequest with
        {
            OutputPath = mix, Tracks = [new ExportTrack(0, 1), new ExportTrack(1, 1)]
        }, audioChannels: 1);
        var mixedSamples = await DecodeAudioAsync(tools, mix, 0, Path.Combine(root, "mix.raw"));
        assert(ToneAmplitude(mixedSamples, 440) / sourceAmplitude is > 0.85 and < 1.15 &&
               ToneAmplitude(mixedSamples, 880) / sourceAmplitude is > 0.85 and < 1.15,
            "Mix retains both source gains without automatic normalization");

        var loud = Path.Combine(root, "limited.m4a");
        await service.ExportAsync(selectedRequest with
        {
            OutputPath = loud, Tracks = [new ExportTrack(0, 8), new ExportTrack(1, 8)]
        }, audioChannels: 1);
        var loudSamples = await DecodeAudioAsync(tools, loud, 0, Path.Combine(root, "limited.raw"));
        assert(loudSamples.All(float.IsFinite) && loudSamples.Max(sample => Math.Abs(sample)) < 1.1,
            "Limiter contains an overdriven mix, allowing AAC reconstruction overshoot");

        var mute = Path.Combine(root, "muted.m4a");
        await service.ExportAsync(selectedRequest with { OutputPath = mute, Tracks = [new ExportTrack(0, 0)] }, 1);
        var mutedSamples = await DecodeAudioAsync(tools, mute, 0, Path.Combine(root, "muted.raw"));
        assert(mutedSamples.All(sample => Math.Abs(sample) < 0.0001), "Zero gain produces silent audio");
        var silentVideo = Path.Combine(root, "silent.mp4");
        await service.ExportAsync(selectedRequest with { OutputPath = silentVideo, AudioOnly = false, Tracks = [] });
        assert((await tools.ProbeAsync(silentVideo)).Tracks.Count == 0, "Video export allows no selected audio");

        var trim = Path.Combine(root, "trim.mkv");
        await tools.TrimArchiveAsync(source, trim, 0.37, 2.25, true);
        var trimInfo = await tools.ProbeAsync(trim);
        assert(trimInfo.HasVideo && trimInfo.Tracks.Count == 2 && Math.Abs(trimInfo.DurationSeconds - 2.25) < 0.10,
            "Archive trim retains both independent audio tracks at requested duration");
        assert(trimInfo.Tracks.Select(track => track.Name).SequenceEqual(info.Tracks.Select(track => track.Name)), "Archive trim preserves track titles");
        var archiveTone0 = await DecodeAudioAsync(tools, trim, 0, Path.Combine(root, "archive0.raw"));
        var archiveTone1 = await DecodeAudioAsync(tools, trim, 1, Path.Combine(root, "archive1.raw"));
        assert(ToneAmplitude(archiveTone0, 440) > 0.1 && ToneAmplitude(archiveTone0, 880) < 0.003 &&
               ToneAmplitude(archiveTone1, 880) > 0.1 && ToneAmplitude(archiveTone1, 440) < 0.003,
            "Archive tracks remain independently selectable, with no mixing");
        var concat = Path.Combine(root, "concat.mkv");
        await tools.ConcatenateAsync([trim, trim], concat, true);
        var concatInfo = await tools.ProbeAsync(concat);
        assert(concatInfo.Tracks.Count == 2 && Math.Abs(concatInfo.DurationSeconds - 2 * trimInfo.DurationSeconds) < 0.15,
            "Concat safely handles apostrophes and Unicode and preserves every audio stream");
        assert(concatInfo.Tracks.Select(track => track.Name).SequenceEqual(info.Tracks.Select(track => track.Name)), "Concat preserves track titles");
        var audioArchive = Path.Combine(root, "audio archive.mka");
        await tools.TrimArchiveAsync(source, audioArchive, 0.37, 2.25, false);
        var audioArchiveInfo = await tools.ProbeAsync(audioArchive);
        assert(!audioArchiveInfo.HasVideo && audioArchiveInfo.Tracks.Count == 2, "Audio archive preserves all audio and excludes video");

        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { Tracks = [] }), assert, "Audio-only export rejects no selection");
        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { Tracks = [new ExportTrack(4, 1)] }), assert, "Invalid track index rejected");
        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { Tracks = [new ExportTrack(0, double.NaN)] }), assert, "Nonfinite gain rejected");
        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { Tracks = [new ExportTrack(0, -1)] }), assert, "Negative gain rejected");
        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { Tracks = [new ExportTrack(0, 1), new ExportTrack(0, 1)] }), assert, "Duplicate audio selection rejected");
        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { StartSeconds = double.NaN }), assert, "Nonfinite start rejected");
        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { EndSeconds = double.PositiveInfinity }), assert, "Nonfinite end rejected");
        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { EndSeconds = 30 }), assert, "Source duration bounds checked");
        await RejectAsync<ArgumentException>(() => service.ExportAsync(selectedRequest with { OutputPath = source }), assert, "Export cannot replace source");
        await RejectAsync<IOException>(() => service.ExportAsync(selectedRequest), assert, "Existing output is never overwritten");
        await RejectAsync<ArgumentException>(() => tools.TrimArchiveAsync(source, trim, 0, 50, true), assert, "Archive trim rejects range beyond source");
        await RejectAsync<ArgumentException>(() => tools.ConcatenateAsync([source], source, true), assert, "Concat cannot overwrite its source");
        await RejectAsync<ArgumentException>(() => tools.ConcatenateAsync([source + "\nfile 'bad'"], concat, true), assert, "Concat rejects line injection");
        try
        {
            await tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "not_a_real_filter", "-f", "null", "-"]);
            assert(false, "FFmpeg failure provides bounded diagnostics");
        }
        catch (FfmpegException error)
        {
            assert(error.ExitCode != 0 && error.Diagnostics.Length is > 0 and <= 65536,
                "FFmpeg failure provides bounded diagnostics");
        }

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            var timer = Stopwatch.StartNew();
            await RejectAsync<OperationCanceledException>(() => tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-re",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=60", "-f", "null", "-"], cancellation.Token),
                assert, "Cancellation terminates an active FFmpeg process");
            assert(timer.Elapsed < TimeSpan.FromSeconds(5), "Cancellation finishes promptly");
        }
        var canceledOutput = Path.Combine(root, "canceled.mp4");
        using (var cancellation = new CancellationTokenSource())
        {
            await RejectAsync<OperationCanceledException>(() => service.ExportAsync(selectedRequest with
            {
                AudioOnly = false, OutputPath = canceledOutput
            }, new InlineProgress(value => { if (value > 0.01) cancellation.Cancel(); }), cancellation.Token),
                assert, "Cancellation during an export aborts publication");
            assert(!File.Exists(canceledOutput), "Canceled export publishes no output");
        }
        assert(!Directory.EnumerateFiles(root).Any(path => path.EndsWith(".partial", StringComparison.Ordinal) ||
            path.EndsWith(".ffconcat", StringComparison.Ordinal) || path.Contains(".passlog", StringComparison.Ordinal)),
            "Completed and canceled media operations clean their temporary files");
        assert((await HashAsync(source)).SequenceEqual(originalHash), "Exports, trims, concat, and cancellation preserve original bytes");
        await CheckLibraryAsync(root, source, assert);
        await CheckSegmentTimelineAsync(tools, root, assert);
    }

    private static async Task CheckSegmentTimelineAsync(FfmpegTools tools, string root, Action<bool, string> assert)
    {
        foreach (var video in new[] { false, true })
        {
            var folder = Path.Combine(root, video ? "video timeline" : "audio timeline");
            Directory.CreateDirectory(folder);
            var csv = Path.Combine(folder, "segments.csv");
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-n" };
            if (video) args.AddRange(["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30"]);
            args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "60"]);
            if (video) args.AddRange(["-map", "0:v", "-c:v", "libx264", "-preset", "ultrafast",
                "-g", "60", "-bf", "0", "-force_key_frames", "expr:gte(t,n_forced*2)"]);
            args.AddRange(["-map", video ? "1:a" : "0:a", "-map", video ? "2:a" : "1:a",
                "-c:a", "aac", "-f", "segment", "-segment_time", "2", "-reset_timestamps", "1",
                "-segment_format", "matroska", "-segment_list_type", "csv", "-segment_list", csv,
                Path.Combine(folder, "part_%08d.mkv")]);
            await tools.RunAsync(args);
            var segments = File.ReadLines(csv).Select(line =>
            {
                var fields = line.Split(',');
                var path = Path.Combine(folder, fields[0].Trim('"'));
                return new SegmentInfo(path, double.Parse(fields[1], CultureInfo.InvariantCulture),
                    double.Parse(fields[2], CultureInfo.InvariantCulture), new FileInfo(path).Length);
            }).ToArray();
            assert(segments.Length >= 30, "Timeline fixture spans at least 30 real encoder boundaries");
            foreach (var tail in new[] { false, true })
            {
                var selected = tail ? segments[^10..] : segments;
                var expected = selected[^1].EndSeconds - selected[0].StartSeconds;
                var joined = Path.Combine(folder, tail ? "tail.mkv" : "whole.mkv");
                await tools.ConcatenateSegmentsAsync(selected, joined, video);
                using var packets = await ProbeJsonAsync(tools, joined, ["-show_packets", "-show_entries",
                    "packet=stream_index,pts_time,duration_time"]);
                var tracks = packets.RootElement.GetProperty("packets").EnumerateArray()
                    .GroupBy(packet => packet.GetProperty("stream_index").GetInt32()).ToArray();
                assert(tracks.Length == (video ? 3 : 2), "Timed concatenation preserves every independent stream");
                foreach (var track in tracks)
                {
                    var times = track.Select(packet => double.Parse(packet.GetProperty("pts_time").GetString()!,
                        CultureInfo.InvariantCulture)).Order().ToArray();
                    assert(Math.Abs(times[0]) < 0.05 && Math.Abs(times[^1] - expected) < 0.08,
                        "Stream timeline retains its start and end without cumulative AAC padding drift");
                    assert(times.Zip(times.Skip(1)).All(pair => pair.Second - pair.First < 0.05),
                        "Every segment boundary preserves continuous packet timing");
                    if (video && track.Key == 0)
                        assert(times.Length == (tail ? 600 : 1800), "Concatenation retains every captured video frame");
                }
            }
        }
    }

    private static async Task CheckLibraryAsync(string workDir, string source, Action<bool, string> assert)
    {
        var libraryRoot = Path.Combine(workDir, "library");
        var library = new RecordingLibrary(libraryRoot);
        var created = DateTimeOffset.Now;
        var destination = library.CreateRecordingPath(created, ".mkv");
        File.Copy(source, destination);
        var manifest = new RecordingManifest { CreatedAt = created, MediaPath = destination, DurationSeconds = 8, HasVideo = true, Title = "Original" };
        var saved = library.Save(manifest);
        var sidecar = library.GetManifestPath(saved.Id);
        assert(Path.GetFileName(sidecar) == saved.Id + ".recording.json" && File.Exists(sidecar), "Save creates the documented original sidecar");
        assert(library.List().Count == 1, "Library lists only original manifests");
        var recorderSidecar = destination + ".recording.json";
        File.Move(sidecar, recorderSidecar);
        sidecar = recorderSidecar;
        assert(library.List().Count == 1 && library.GetManifestPath(saved.Id) == recorderSidecar,
            "Library accepts the recorder's adjacent media sidecar convention");
        var renamed = library.Rename(saved, "Renamed recording");
        assert(renamed.Title == "Renamed recording" && library.List()[0].Title == "Renamed recording" && File.Exists(destination), "Rename updates metadata and preserves media path");
        saved.Title = "Updated";
        await library.SaveAsync(saved);
        assert(library.List()[0].Title == "Updated", "SaveAsync atomically updates existing metadata");
        await RejectAsync<IOException>(() => library.AddAsync(saved), assert, "Add refuses duplicate identifiers");
        await RejectAsync<ArgumentException>(() => library.AddAsync(new RecordingManifest { MediaPath = source, CreatedAt = created }), assert, "Library rejects originals outside its root");
        await RejectAsync<ArgumentException>(() => library.AddAsync(new RecordingManifest
        {
            MediaPath = Path.Combine(libraryRoot, "..", Path.GetFileName(source)), CreatedAt = created
        }), assert, "Library rejects traversal");
        await RejectAsync<ArgumentException>(() => library.AddAsync(new RecordingManifest
        {
            Id = "../escape", MediaPath = destination, CreatedAt = created
        }), assert, "Library rejects identifier traversal");
        await RejectAsync<ArgumentException>(() => Task.Run(() => library.Delete("../escape")), assert, "Delete rejects unsafe identifiers");
        var siblingExport = Path.Combine(Path.GetDirectoryName(destination)!, "user-export.mp4");
        await File.WriteAllTextAsync(siblingExport, "export must survive library deletion");
        var malformed = Path.Combine(Path.GetDirectoryName(destination)!, "broken.recording.json");
        await File.WriteAllTextAsync(malformed, "{bad json");
        assert(library.List().Count == 1 && library.Warnings.Count == 1, "Malformed sidecar is skipped with a diagnostic");
        var unsafeManifest = new RecordingManifest { Id = "unsafe", CreatedAt = created, MediaPath = source };
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(destination)!, "unsafe.recording.json"), JsonSerializer.Serialize(unsafeManifest));
        assert(library.List().Count == 1 && library.Warnings.Count == 2, "Sidecar cannot point deletion outside the library");
        await RejectAsync<FileNotFoundException>(() => Task.Run(() => library.Delete("unsafe")), assert, "Delete refuses an unsafe stored manifest");
        // Delete uses persisted metadata, never a mutable caller-provided MediaPath.
        saved.MediaPath = source;
        library.Delete(saved);
        assert(!File.Exists(destination) && !File.Exists(sidecar) && File.Exists(source) && File.Exists(siblingExport),
            "Delete removes only persisted original and sidecar, preserving exports and external media");
        assert(library.List().Count == 0, "Deleted original no longer appears in library");
    }

    private static async Task<byte[]> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    private static async Task<float[]> DecodeAudioAsync(FfmpegTools tools, string path, int track, string rawPath)
    {
        await tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-n", "-ss", "0.1", "-i", path,
            "-map", "0:a:" + track.ToString(CultureInfo.InvariantCulture), "-t", "1", "-ac", "1", "-ar", "48000", "-f", "f32le", rawPath]);
        var bytes = await File.ReadAllBytesAsync(rawPath);
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    private static double ToneAmplitude(float[] samples, int frequency)
    {
        if (samples.Length == 0) return 0;
        double real = 0, imaginary = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var angle = 2 * Math.PI * frequency * i / 48_000;
            real += samples[i] * Math.Cos(angle);
            imaginary += samples[i] * Math.Sin(angle);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }

    private static async Task<JsonDocument> ProbeJsonAsync(FfmpegTools tools, string path, string[]? query = null)
    {
        var start = new ProcessStartInfo(tools.FfprobePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-v", "error" }.Concat(query ?? ["-show_streams"])
            .Concat(["-of", "json", "-i", path])) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFprobe did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await errors);
        return JsonDocument.Parse(await output);
    }

    private static async Task RejectAsync<T>(Func<Task> action, Action<bool, string> assert, string description) where T : Exception
    {
        try { await action(); }
        catch (T) { assert(true, description); return; }
        assert(false, description);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
