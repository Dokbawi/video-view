namespace VideoView.Platform.Video;

/// <summary>A read-only frame valid through Dispose, including during capture shutdown.</summary>
public sealed class VideoFrameLease : IDisposable
{
    private CaptureFrames? _owner;
    private readonly CaptureFrames.Slot? _slot;
    internal VideoFrameLease(VideoFrame frame, CaptureFrames? owner = null, CaptureFrames.Slot? slot = null)
    { Frame = frame; _owner = owner; _slot = slot; }
    public VideoFrame Frame { get; }
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_slot!);
}

/// <summary>One capture producer, asynchronous encoder and occasional preview readers. Never wait for a reader.</summary>
internal sealed class CaptureFrames
{
    internal sealed class Slot
    {
        public byte[] Pixels = [];
        public int Readers;
        public VideoFrame? Frame;
    }
    private readonly object _gate = new();
    private readonly Slot[] _slots = [new(), new(), new(), new()];
    private Slot? _latest;
    private VideoFrame? _snapshot;

    // Only the producer calls BeginWrite/Publish. A non-latest, unheld slot cannot gain a reader.
    internal Slot? BeginWrite(int length)
    {
        Slot? slot;
        lock (_gate) slot = Array.Find(_slots, s => s != _latest && s.Readers == 0);
        if (slot != null && slot.Pixels.Length != length) slot.Pixels = GC.AllocateUninitializedArray<byte>(length);
        return slot;
    }
    internal void Publish(Slot slot, int width, int height, long sequence)
    {
        lock (_gate)
        {
            slot.Frame = new(slot.Pixels, width, height, sequence);
            _latest = slot;
            _snapshot = null;
        }
    }
    public VideoFrameLease? Acquire()
    {
        lock (_gate)
        {
            if (_latest?.Frame is not { } frame) return null;
            _latest.Readers++;
            return new(frame, this, _latest);
        }
    }
    public VideoFrame? Snapshot()
    {
        // Compatibility API: callers that keep a snapshot never observe a recycled buffer.
        lock (_gate) return _latest?.Frame is { } frame ? _snapshot ??= frame with { Pixels = (byte[])frame.Pixels.Clone() } : null;
    }
    internal void Release(Slot slot) { lock (_gate) slot.Readers--; }
    public void Clear() { lock (_gate) { _latest = null; _snapshot = null; } }
}
