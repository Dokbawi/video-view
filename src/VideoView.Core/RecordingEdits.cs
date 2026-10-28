namespace VideoView.Core;

/// <summary>Non-destructive edit decisions stored alongside the original recording.</summary>
public sealed class RecordingEdits
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Position { get; set; }
    public List<ExportTrack> Tracks { get; set; } = [];
    public List<double> Bookmarks { get; set; } = [];

    public RecordingEdits Normalize(RecordingManifest source)
    {
        var duration = Math.Max(0, source.DurationSeconds);
        double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, duration) : 0;
        var start = Clamp(Start); var end = Clamp(End);
        if (end - start < Math.Min(0.05, duration)) { start = 0; end = duration; }
        return new()
        {
            Start = start, End = end, Position = Clamp(Position),
            Tracks = source.Tracks.Select(track =>
            {
                var saved = Tracks?.FirstOrDefault(t => t != null && t.Index == track.Index);
                return new ExportTrack(track.Index,
                    saved != null && double.IsFinite(saved.Gain) ? Math.Clamp(saved.Gain, 0, 2) : 1,
                    saved?.Included ?? true);
            }).ToList(),
            Bookmarks = (Bookmarks ?? []).Where(double.IsFinite).Where(p => p >= 0 && p <= duration)
                .Select(p => Math.Round(p, 3)).Distinct().Order().Take(500).ToList()
        };
    }
}
