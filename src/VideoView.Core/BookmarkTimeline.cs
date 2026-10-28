namespace VideoView.Core;

public static class BookmarkTimeline
{
    /// <summary>Maps session-clock marks into a trimmed archive, including gaps removed during recovery.</summary>
    public static List<double> Map(IReadOnlyList<SegmentInfo> segments, double start, double end,
        double outputDuration, IEnumerable<double>? bookmarks)
    {
        if (segments.Count == 0 || bookmarks == null) return [];
        var offset = Math.Max(0, start - segments[0].StartSeconds);
        var mapped = new List<double>();
        foreach (var mark in bookmarks.Where(double.IsFinite).Where(t => t >= start && t < end).Distinct().Order())
        {
            double elapsed = 0;
            foreach (var segment in segments)
            {
                if (mark >= segment.StartSeconds && mark < segment.EndSeconds)
                {
                    var position = elapsed + mark - segment.StartSeconds - offset;
                    if (position >= 0 && position < outputDuration) mapped.Add(Math.Round(position, 3));
                    break;
                }
                elapsed += segment.Duration;
            }
        }
        return mapped.Distinct().Take(500).ToList();
    }
}
