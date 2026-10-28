namespace VideoView.Core;

/// <summary>Pure retention policy. Only closed, unpinned buffer segments can expire.</summary>
public static class ReplayWindow
{
    public static IReadOnlyList<SegmentInfo> Select(IEnumerable<SegmentInfo> segments, double endSeconds, double lengthSeconds)
        => segments.Where(s => s.EndSeconds > Math.Max(0, endSeconds - lengthSeconds) && s.StartSeconds < endSeconds)
            .OrderBy(s => s.StartSeconds).ToArray();

    public static IReadOnlyList<SegmentInfo> Expired(IEnumerable<SegmentInfo> segments, double endSeconds,
        double lengthSeconds, long maxBytes, ISet<string> pinned)
    {
        var all = segments.OrderBy(s => s.StartSeconds).ToList();
        var expired = new List<SegmentInfo>();
        var bytes = all.Sum(s => s.Bytes);
        foreach (var segment in all)
        {
            if (pinned.Contains(segment.Path)) continue;
            if (segment.EndSeconds <= endSeconds - lengthSeconds || bytes > maxBytes)
            {
                expired.Add(segment);
                bytes -= segment.Bytes;
            }
        }
        return expired;
    }
}
