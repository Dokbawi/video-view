namespace VideoView.Platform.Video;

public enum CaptureTargetKind { Monitor, Window }

/// <summary>A snapshot of a native capture target. Refresh the catalog after display/window changes.</summary>
public sealed record CaptureTarget(string Id, string Name, CaptureTargetKind Kind, nint Handle);

/// <summary>An owned, tightly packed BGRA8 snapshot (stride = Width * 4).</summary>
/// <remarks>
/// The producer never modifies or reuses Pixels after publication. Consumers must treat this
/// array as read-only; copy it before editing. Keeping a frame is safe across Stop/Dispose.
/// </remarks>
public sealed record VideoFrame(byte[] Pixels, int Width, int Height, long Sequence);
