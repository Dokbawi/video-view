namespace VideoView.Platform.Video;

public enum CaptureTargetKind { Monitor, Window }

/// <summary>A snapshot of a native capture target. Refresh the catalog after display/window changes.</summary>
public sealed record CaptureTarget(string Id, string Name, CaptureTargetKind Kind, nint Handle);

/// <summary>A tightly packed BGRA8 frame (stride = Width * 4).</summary>
/// <remarks>
/// LatestFrame snapshots own their pixels. Frames read through a VideoFrameLease remain
/// valid until that lease is disposed. Both are read-only to consumers.
/// </remarks>
public sealed record VideoFrame(byte[] Pixels, int Width, int Height, long Sequence);
