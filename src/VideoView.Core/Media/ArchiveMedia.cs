using System.Globalization;
using System.Text;

namespace VideoView.Core.Media;

internal static class ArchiveMedia
{
    internal static async Task<string> ConcatenateAsync(FfmpegTools tools, IReadOnlyList<string> segmentPaths,
        string outputPath, bool hasVideo, CancellationToken cancellationToken,
        IReadOnlyList<double>? segmentDurations = null)
    {
        ArgumentNullException.ThrowIfNull(segmentPaths);
        if (segmentPaths.Count == 0) throw new ArgumentException("At least one segment is required.", nameof(segmentPaths));
        if (segmentDurations != null && (segmentDurations.Count != segmentPaths.Count ||
            segmentDurations.Any(duration => !double.IsFinite(duration) || duration <= 0)))
            throw new ArgumentException("Every segment needs a finite positive duration.", nameof(segmentDurations));
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = segmentPaths.Select(MediaPaths.ExistingFile).ToArray();
        var output = MediaPaths.NewOutput(outputPath, inputs);
        var muxer = Muxer(output);
        var partial = MediaPaths.TemporarySibling(output, ".partial");
        var list = MediaPaths.TemporarySibling(output, ".ffconcat");
        try
        {
            // ffconcat uses its own quoting, not shell quoting. Newlines were rejected by MediaPaths.
            var content = new StringBuilder("ffconcat version 1.0\n");
            for (var index = 0; index < inputs.Length; index++)
            {
                content.Append("file '").Append(inputs[index].Replace('\\', '/')
                    .Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
                // Container duration includes AAC tail padding; using it for every boundary
                // accumulates drift. Segment CSV times preserve the common capture clock.
                // Keep each file's zero origin instead of subtracting its earliest stream PTS.
                if (segmentDurations != null)
                    content.Append("inpoint 0\nduration ").Append(Number(segmentDurations[index])).Append('\n');
            }
            await File.WriteAllTextAsync(list, content.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "warning", "-n",
                "-f", "concat", "-safe", "0", "-i", list };
            if (hasVideo) args.AddRange(["-map", "0:V:0"]);
            args.AddRange(["-map", hasVideo ? "0:a?" : "0:a", "-c", "copy", "-map_metadata", "0"]);
            if (muxer is "mp4" or "ipod") args.AddRange(["-movflags", "+faststart"]);
            args.AddRange(["-f", muxer, partial]);
            await tools.RunAsync(args, cancellationToken).ConfigureAwait(false);
            Publish(partial, output, cancellationToken);
            return output;
        }
        finally
        {
            await MediaPaths.DeleteTemporaryAsync(partial).ConfigureAwait(false);
            await MediaPaths.DeleteTemporaryAsync(list).ConfigureAwait(false);
        }
    }

    internal static async Task<string> TrimAsync(FfmpegTools tools, string inputPath, string outputPath,
        double startSeconds, double durationSeconds, bool hasVideo, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            !double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
            !double.IsFinite(startSeconds + durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Trim range must be finite and positive.");
        cancellationToken.ThrowIfCancellationRequested();
        var input = MediaPaths.ExistingFile(inputPath);
        var info = await tools.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (startSeconds >= info.DurationSeconds || startSeconds + durationSeconds > info.DurationSeconds + 0.001)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Trim range extends beyond the source duration.");
        if (hasVideo && !info.HasVideo) throw new ArgumentException("The source has no video track.", nameof(hasVideo));
        if (!hasVideo && info.Tracks.Count == 0) throw new ArgumentException("The source has no audio tracks.");
        var output = MediaPaths.NewOutput(outputPath, [input]);
        var muxer = Muxer(output);
        if (hasVideo && muxer == "ipod") throw new ArgumentException("Video archives require MP4 or Matroska.", nameof(outputPath));
        var partial = MediaPaths.TemporarySibling(output, ".partial");
        try
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "warning", "-n",
                "-ss", Number(startSeconds), "-accurate_seek", "-i", input,
                "-t", Number(durationSeconds), "-map_metadata", "0", "-map_chapters", "-1" };
            if (hasVideo)
                args.AddRange(["-map", "0:V:0", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p"]);
            else args.Add("-vn");
            args.AddRange(["-map", hasVideo ? "0:a?" : "0:a", "-c:a", "aac", "-b:a", "192k"]);
            foreach (var track in info.Tracks)
            {
                args.AddRange([$"-metadata:s:a:{track.Index}", "title=" + track.Name]);
                // MP4 commonly exposes audio names through handler_name instead of title.
                args.AddRange([$"-metadata:s:a:{track.Index}", "handler_name=" + track.Name]);
            }
            if (muxer is "mp4" or "ipod") args.AddRange(["-movflags", "+faststart"]);
            args.AddRange(["-f", muxer, partial]);
            await tools.RunAsync(args, cancellationToken).ConfigureAwait(false);
            Publish(partial, output, cancellationToken);
            return output;
        }
        finally { await MediaPaths.DeleteTemporaryAsync(partial).ConfigureAwait(false); }
    }

    internal static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string Muxer(string output) => Path.GetExtension(output).ToLowerInvariant() switch
    {
        ".mp4" => "mp4",
        ".m4a" => "ipod",
        ".mkv" or ".mka" => "matroska",
        _ => throw new ArgumentException("Archive output must use .mp4, .m4a, .mkv, or .mka.", nameof(output))
    };

    internal static void Publish(string partial, string output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
            throw new IOException("FFmpeg did not produce a nonempty output file.");
        File.Move(partial, output, overwrite: false);
    }
}
