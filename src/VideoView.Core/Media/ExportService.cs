using System.Globalization;
using System.Text;

namespace VideoView.Core.Media;

/// <summary>Creates bounded sharing copies. Originals and existing output files are never replaced.</summary>
public sealed class ExportService
{
    private const int MaximumAttempts = 4;
    private readonly FfmpegTools _tools;

    public ExportService(FfmpegTools tools) => _tools = tools ?? throw new ArgumentNullException(nameof(tools));

    /// <summary>Exports stereo audio by default. Progress ranges from zero through one.</summary>
    public Task<string> ExportAsync(ExportRequest request, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) => ExportAsync(request, 2, progress, cancellationToken);

    /// <summary>Audio channels must be one (mono) or two (stereo). TargetMegabytes uses decimal MB.</summary>
    public async Task<string> ExportAsync(ExportRequest request, int audioChannels,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Validate(request, audioChannels);
        cancellationToken.ThrowIfCancellationRequested();
        var source = MediaPaths.ExistingFile(request.Recording.MediaPath);
        var info = await _tools.ProbeAsync(source, cancellationToken).ConfigureAwait(false);
        if (request.StartSeconds >= info.DurationSeconds || request.EndSeconds > info.DurationSeconds + 0.001)
            throw new ArgumentOutOfRangeException(nameof(request), "Export range extends beyond the source duration.");
        if (!request.AudioOnly && !info.HasVideo)
            throw new ArgumentException("The source has no video. Choose an audio-only export.", nameof(request));
        if (request.Tracks.Any(track => track.Index >= info.Tracks.Count))
            throw new ArgumentException("An audio track index does not exist in the source.", nameof(request));
        // A selected zero-gain track is still an intentional silent audio stream.
        var tracks = request.Tracks.Where(track => track.Included).ToArray();
        if (request.AudioOnly && tracks.Length == 0)
            throw new ArgumentException("An audio-only export requires at least one selected audio track.", nameof(request));
        var output = MediaPaths.NewOutput(request.OutputPath, [source]);
        var extension = request.AudioOnly ? ".m4a" : ".mp4";
        if (!string.Equals(Path.GetExtension(output), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"This export requires a {extension} output path.", nameof(request));

        var duration = request.EndSeconds - request.StartSeconds;
        var targetBytes = checked((long)request.TargetMegabytes * 1_000_000);
        // Reserve space for container tables, AAC framing, and rate-control variance.
        var totalBitrate = (targetBytes * 0.94 - 16_384) * 8 / duration;
        var audioMinimum = audioChannels == 1 ? 16_000 : 24_000;
        var audioBitrate = tracks.Length == 0 ? 0 : (int)Math.Min(audioChannels == 1 ? 96_000 : 192_000,
            Math.Max(audioMinimum, totalBitrate * (request.AudioOnly ? 1 : 0.20)));
        var videoBitrate = request.AudioOnly ? 0 : (int)Math.Min(40_000_000, Math.Floor(totalBitrate - audioBitrate));
        if ((request.AudioOnly && totalBitrate < audioMinimum) || (!request.AudioOnly && videoBitrate < 32_000))
            throw new InvalidOperationException("The size limit is too small for this duration. Shorten the clip or increase its size limit.");

        var partial = MediaPaths.TemporarySibling(output, ".partial");
        var passLog = MediaPaths.TemporarySibling(output, ".passlog");
        var lastProgress = 0d;
        void Report(double value)
        {
            value = Math.Clamp(value, lastProgress, 1);
            if (value > lastProgress || value == 0)
            {
                lastProgress = value;
                progress?.Report(value);
            }
        }
        void OnProgress(string line, int attempt, bool firstPass)
        {
            const string prefix = "out_time_us=";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) ||
                !double.TryParse(line.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var micros) ||
                !double.IsFinite(micros)) return;
            var fraction = Math.Clamp(micros / 1_000_000 / duration, 0, 1);
            // Reserve room for retries without making progress move backwards.
            var attemptStart = 1 - Math.Pow(0.1, attempt);
            var attemptSpan = 0.9 * Math.Pow(0.1, attempt);
            var step = request.AudioOnly ? fraction : (firstPass ? fraction * 0.4 : 0.4 + fraction * 0.6);
            Report(Math.Min(0.999, attemptStart + attemptSpan * step));
        }

        Report(0);
        try
        {
            for (var attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!request.AudioOnly)
                {
                    var firstPass = BuildArgs(request, source, partial, passLog, tracks, audioChannels,
                        audioBitrate, videoBitrate, firstPass: true);
                    await _tools.RunWithProgressAsync(firstPass, line => OnProgress(line, attempt, true), cancellationToken).ConfigureAwait(false);
                }
                var args = BuildArgs(request, source, partial, passLog, tracks, audioChannels,
                    audioBitrate, videoBitrate, firstPass: false);
                await _tools.RunWithProgressAsync(args, line => OnProgress(line, attempt, false), cancellationToken).ConfigureAwait(false);
                var size = new FileInfo(partial).Length;
                if (size > 0 && size <= targetBytes)
                {
                    ArchiveMedia.Publish(partial, output, cancellationToken);
                    Report(1);
                    return output;
                }
                await MediaPaths.DeleteTemporaryAsync(partial).ConfigureAwait(false);
                if (size == 0) throw new IOException("FFmpeg produced an empty export.");
                // Re-run both passes with a smaller budget; never publish an oversized result.
                var ratio = Math.Min(0.85, targetBytes / (double)size * 0.90);
                if (request.AudioOnly)
                {
                    var next = Math.Max(audioMinimum, (int)(audioBitrate * ratio));
                    if (next >= audioBitrate) break;
                    audioBitrate = next;
                }
                else
                {
                    var nextVideo = Math.Max(32_000, (int)(videoBitrate * ratio));
                    var nextAudio = audioBitrate == 0 ? 0 : Math.Max(audioMinimum, (int)(audioBitrate * ratio));
                    if (nextVideo >= videoBitrate && nextAudio >= audioBitrate) break;
                    videoBitrate = nextVideo;
                    audioBitrate = nextAudio;
                }
            }
            throw new InvalidOperationException("The encoded clip cannot meet the requested size limit. Shorten it or increase the limit.");
        }
        finally
        {
            await MediaPaths.DeleteTemporaryAsync(partial).ConfigureAwait(false);
            // x264 owns these files only; never enumerate or delete unrelated output files.
            foreach (var suffix in new[] { "", "-0.log", "-0.log.mbtree", "-0.log.temp", "-0.log.mbtree.temp" })
                await MediaPaths.DeleteTemporaryAsync(passLog + suffix).ConfigureAwait(false);
        }
    }

    public Task<string> ConcatenateAsync(IReadOnlyList<string> segmentPaths, string outputPath,
        bool hasVideo, CancellationToken cancellationToken = default) =>
        _tools.ConcatenateAsync(segmentPaths, outputPath, hasVideo, cancellationToken);

    private static List<string> BuildArgs(ExportRequest request, string source, string partial, string passLog,
        IReadOnlyList<ExportTrack> tracks, int channels, int audioBitrate, int videoBitrate, bool firstPass)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "warning", "-n",
            "-filter_threads", "1", "-filter_complex_threads", "1",
            "-progress", "pipe:1", "-nostats", "-ss", ArchiveMedia.Number(request.StartSeconds),
            "-accurate_seek", "-i", source, "-t", ArchiveMedia.Number(request.EndSeconds - request.StartSeconds),
            "-map_metadata", "-1", "-map_chapters", "-1" };
        if (!request.AudioOnly)
        {
            args.AddRange(["-map", "0:V:0", "-c:v", "libx264", "-preset", "medium", "-threads", "2",
                "-b:v", videoBitrate.ToString(CultureInfo.InvariantCulture),
                "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p",
                "-pass", firstPass ? "1" : "2", "-passlogfile", passLog]);
        }
        else args.Add("-vn");

        if (firstPass || tracks.Count == 0) args.Add("-an");
        else
        {
            var graph = new StringBuilder();
            for (var i = 0; i < tracks.Count; i++)
            {
                graph.Append(CultureInfo.InvariantCulture,
                    $"[0:a:{tracks[i].Index}]aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts={(channels == 1 ? "mono" : "stereo")},volume={tracks[i].Gain:G17}[a{i}];");
            }
            for (var i = 0; i < tracks.Count; i++) graph.Append(CultureInfo.InvariantCulture, $"[a{i}]");
            graph.Append(CultureInfo.InvariantCulture,
                $"amix=inputs={tracks.Count}:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95:level=0:latency=1,apad[mix]");
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[mix]", "-c:a", "aac",
                "-b:a", audioBitrate.ToString(CultureInfo.InvariantCulture), "-ar", "48000",
                "-ac", channels.ToString(CultureInfo.InvariantCulture)]);
        }
        if (firstPass) args.AddRange(["-f", "null", "-"]);
        else args.AddRange(["-movflags", "+faststart", "-f", request.AudioOnly ? "ipod" : "mp4", partial]);
        return args;
    }

    private static void Validate(ExportRequest request, int audioChannels)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Recording);
        ArgumentNullException.ThrowIfNull(request.Tracks);
        if (audioChannels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(audioChannels));
        if (!double.IsFinite(request.StartSeconds) || !double.IsFinite(request.EndSeconds) ||
            request.StartSeconds < 0 || request.EndSeconds <= request.StartSeconds)
            throw new ArgumentOutOfRangeException(nameof(request), "Export times must be finite, nonnegative, and increasing.");
        if (request.TargetMegabytes is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(request), "Target size must be between 1 and 10000 MB.");
        if (request.Tracks.Any(track => track is null || track.Index < 0 || !double.IsFinite(track.Gain) || track.Gain < 0))
            throw new ArgumentException("Audio indices and gains must be finite and nonnegative.", nameof(request));
        if (request.Tracks.Select(track => track.Index).Distinct().Count() != request.Tracks.Count)
            throw new ArgumentException("An audio track can only be selected once.", nameof(request));
    }
}
