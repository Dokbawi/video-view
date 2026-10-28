namespace VideoView.Core;

/// <summary>Immutable, atomically published telemetry. Bytes count logical ring files,
/// not additional disk allocation of hard links. Open segment sizes are sampled.</summary>
public sealed record BufferStats
{
    public long BufferBytes { get; init; }
    public long LimitBytes { get; init; }
    public long ClosedBytes { get; init; }
    public long PinnedBytes { get; init; }
    public int ClosedSegments { get; init; }
    public double RetainedSeconds { get; init; }
    public double MaintenanceMilliseconds { get; init; }
    public double MaxMaintenanceMilliseconds { get; init; }
    public double SegmentLockMilliseconds { get; init; }
    public double MaxSegmentLockMilliseconds { get; init; }
    public long MaintenanceCount { get; init; }
    public long HardLinkCount { get; init; }
    public long CopyCount { get; init; }
    public long CopiedBytes { get; init; }
    public long CsvBytesRead { get; init; }
    public long CsvRowsRead { get; init; }
    public long DiskAuditCount { get; init; }
    public long EvictedSegments { get; init; }
    public DateTimeOffset SampledAt { get; init; }
}
