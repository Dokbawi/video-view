using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VideoView.Core.Media;

public sealed record AudioNormalizationResult(double? InputLufs, double GainDecibels, bool Limited)
{
    public bool Measurable => InputLufs.HasValue;
    public string Description => !Measurable
        ? "무음 또는 측정하기 어려운 짧고 작은 소리 · 자동 보정 생략"
        : $"공유 음량 {GainDecibels:+0.0;-0.0;0.0} dB" +
          (Limited ? " · 피크/증폭 상한 때문에 목표보다 작을 수 있습니다" : " · 약 -16 LUFS 목표");
}

/// <summary>Measures only the selected mix, then applies one bounded gain to preserve its balance and dynamics.</summary>
internal static class AudioNormalization
{
    private const double TargetLufs = -16;
    private const double PeakCeiling = -2;
    private const double MaximumBoost = 36;

    internal static string MixGraph(IReadOnlyList<ExportTrack> tracks, int channels, double duration)
    {
        var graph = new StringBuilder();
        for (var i = 0; i < tracks.Count; i++)
            graph.Append(CultureInfo.InvariantCulture,
                $"[0:a:{tracks[i].Index}]aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts={(channels == 1 ? "mono" : "stereo")},volume={tracks[i].Gain:G17}[a{i}];");
        for (var i = 0; i < tracks.Count; i++) graph.Append(CultureInfo.InvariantCulture, $"[a{i}]");
        graph.Append(CultureInfo.InvariantCulture,
            $"amix=inputs={tracks.Count}:duration=longest:dropout_transition=0:normalize=0,apad,atrim=duration={duration:G17},asetpts=PTS-STARTPTS");
        return graph.ToString();
    }

    internal static async Task<AudioNormalizationResult> MeasureAsync(FfmpegTools tools, ExportRequest request,
        string source, IReadOnlyList<ExportTrack> tracks, int channels, Action<string> onProgress, CancellationToken token)
    {
        var duration = request.EndSeconds - request.StartSeconds;
        var graph = MixGraph(tracks, channels, duration) + ",loudnorm=I=-16:TP=-2:LRA=11:print_format=json[analysis]";
        var diagnostics = await tools.AnalyzeAsync([
            "-hide_banner", "-nostdin", "-v", "info", "-filter_threads", "1", "-filter_complex_threads", "1",
            "-progress", "pipe:1", "-nostats", "-ss", ArchiveMedia.Number(request.StartSeconds),
            "-accurate_seek", "-i", source, "-filter_complex", graph, "-map", "[analysis]",
            "-vn", "-sn", "-dn", "-ar", "48000", "-f", "null", "-"], onProgress, token).ConfigureAwait(false);
        var start = diagnostics.LastIndexOf('{'); var end = diagnostics.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("음량 분석 결과를 읽을 수 없습니다. 보정을 끄거나 다시 시도하세요.");
        using var json = JsonDocument.Parse(diagnostics[start..(end + 1)]);
        var input = ReadLevel(json.RootElement, "input_i");
        var peak = ReadLevel(json.RootElement, "input_tp");
        // R128's gate cannot measure silence, extremely faint signals, or some very short clips.
        // Do not amplify these blindly, and never turn an intentional zero-gain mix into noise.
        if (!double.IsFinite(input) || !double.IsFinite(peak)) return new(null, 0, false);
        var requested = TargetLufs - input;
        var gain = Math.Min(requested, Math.Min(MaximumBoost, PeakCeiling - peak));
        return new(input, gain, gain < requested - 0.1);
    }

    private static double ReadLevel(JsonElement json, string name)
    {
        var text = json.GetProperty(name).GetString();
        if (text == "-inf") return double.NegativeInfinity;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var level) || !double.IsFinite(level))
            throw new InvalidDataException("음량 분석 값이 올바르지 않습니다.");
        return level;
    }
}

public sealed record ExportResult(string Path, AudioNormalizationResult? Normalization);
