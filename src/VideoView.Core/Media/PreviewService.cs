using System.Globalization;
using System.Text;

namespace VideoView.Core.Media;

/// <summary>Preserves encoded video and creates a playable mixed-audio proxy for seeking.</summary>
public sealed class PreviewService(FfmpegTools tools)
{
    public Task<string> CreateAsync(RecordingManifest recording, IReadOnlyList<ExportTrack> tracks,
        string output, CancellationToken token = default) => CreateCoreAsync(recording, tracks, output, 0, null, token);

    public Task<string> CreateRangeAsync(RecordingManifest recording, IReadOnlyList<ExportTrack> tracks,
        string output, double start, double duration, CancellationToken token = default) =>
        CreateCoreAsync(recording, tracks, output, start, duration, token);

    private async Task<string> CreateCoreAsync(RecordingManifest recording, IReadOnlyList<ExportTrack> tracks,
        string output, double start, double? duration, CancellationToken token)
    {
        var source = MediaPaths.ExistingFile(recording.MediaPath);
        var info = await tools.ProbeAsync(source, token).ConfigureAwait(false);
        if (!double.IsFinite(start) || start < 0 || start >= info.DurationSeconds ||
            duration is { } requested && (!double.IsFinite(requested) || requested <= 0))
            throw new ArgumentException("Invalid preview range.");
        var length = Math.Min(duration ?? info.DurationSeconds, info.DurationSeconds - start);
        var selected = tracks.Where(t => t.Included).ToArray();
        if (tracks.Any(t => t.Index < 0 || t.Index >= info.Tracks.Count || !double.IsFinite(t.Gain) || t.Gain < 0 || t.Gain > 2) ||
            tracks.GroupBy(t => t.Index).Any(g => g.Count() > 1)) throw new ArgumentException("Invalid preview track selection.");
        if (!info.HasVideo && selected.Length == 0) throw new ArgumentException("미리 들을 소리를 하나 이상 선택해 주세요.");
        output = MediaPaths.NewOutput(output, [source]);
        var partial = MediaPaths.TemporarySibling(output, ".partial");
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "warning", "-n", "-filter_complex_threads", "1", "-filter_threads", "1" };
        if (duration != null) args.AddRange(["-ss", start.ToString("G17", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", source, "-t", length.ToString("G17", CultureInfo.InvariantCulture)]);
        if (info.HasVideo)
        {
            args.AddRange(["-map", "0:v:0"]);
            if (duration == null) args.AddRange(["-c:v", "copy"]);
            else args.AddRange(["-vf", "scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-tune", "zerolatency", "-threads", "2", "-pix_fmt", "yuv420p"]);
        }
        else args.Add("-vn");
        if (selected.Length > 0)
        {
            var graph = new StringBuilder();
            for (var i = 0; i < selected.Length; i++)
                graph.Append(CultureInfo.InvariantCulture, $"[0:a:{selected[i].Index}]aresample=48000:async=1:first_pts=0,aformat=channel_layouts=stereo,volume={selected[i].Gain:G17}[a{i}];");
            for (var i = 0; i < selected.Length; i++) graph.Append($"[a{i}]");
            graph.Append($"amix=inputs={selected.Length}:duration=longest:normalize=0:dropout_transition=0,alimiter=limit=0.95:level=0:latency=1,apad[mix]");
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[mix]", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        }
        else args.Add("-an");
        args.AddRange(["-movflags", "+faststart", "-f", info.HasVideo ? "mp4" : "ipod", partial]);
        try
        {
            await tools.RunAsync(args, token).ConfigureAwait(false);
            ArchiveMedia.Publish(partial, output, token);
            return output;
        }
        finally { await MediaPaths.DeleteTemporaryAsync(partial).ConfigureAwait(false); }
    }
}
