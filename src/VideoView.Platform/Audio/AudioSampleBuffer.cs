using System;
using System.Threading;

namespace VideoView.Platform.Audio;

/// <summary>A bounded queue for a single master-clock reader; Read never waits for its producer.</summary>
internal sealed class AudioSampleBuffer
{
    internal const int SampleRate = 48_000;
    internal const int Channels = 2;
    internal const int BacklogMilliseconds = 40;
    internal const int RecoveryMilliseconds = 20;
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly float[] _samples = new float[SampleRate * Channels * BacklogMilliseconds / 1_000];
    private int _head;
    private int _count;
    private int _nextChannel;
    private bool _accepting;
    private long _lastWrite;
    private float _peak;
    private long _skippedReadSamples;

    internal AudioSampleBuffer(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    internal int BufferedSamples { get { lock (_gate) return _count; } }

    public float Peak
    {
        get
        {
            lock (_gate)
            {
                var age = _time.GetElapsedTime(_lastWrite).TotalMilliseconds;
                return age >= 150 ? 0 : _peak * (float)(1 - age / 150);
            }
        }
    }

    public void Reset(bool accepting)
    {
        lock (_gate)
        {
            _accepting = accepting;
            _head = _count = _nextChannel = 0;
            _peak = 0;
            _lastWrite = 0;
            Interlocked.Exchange(ref _skippedReadSamples, 0);
        }
    }

    public void Write(float[] samples, int count, bool discontinuity)
    {
        lock (_gate)
        {
            ApplySkippedReads();
            if (!_accepting || count == 0)
                return;
            if (discontinuity || _time.GetElapsedTime(_lastWrite).TotalMilliseconds > BacklogMilliseconds)
                _head = _count = 0;

            var start = 0;
            if (count > _samples.Length)
            {
                var retain = SampleRate * Channels * RecoveryMilliseconds / 1_000;
                start = count - retain;
                count = retain;
                _head = _count = 0;
            }
            // Preserve L/R ordering even when a caller reads an odd number of samples.
            if (_count == 0 && _nextChannel == 1)
            {
                start++;
                count--;
            }
            var overflow = _count + count - _samples.Length;
            if (overflow > 0)
            {
                // Return to a short latency after encoder stalls or clock mismatch rather
                // than staying permanently pinned to the maximum queue delay.
                var retain = SampleRate * Channels * RecoveryMilliseconds / 1_000;
                var drop = (_count + count - retain + 1) & ~1;
                var fromQueue = Math.Min(drop, _count);
                _head = (_head + fromQueue) % _samples.Length;
                _count -= fromQueue;
                start += drop - fromQueue;
                count -= drop - fromQueue;
            }

            var tail = (_head + _count) % _samples.Length;
            var first = Math.Min(count, _samples.Length - tail);
            Array.Copy(samples, start, _samples, tail, first);
            Array.Copy(samples, start + first, _samples, 0, count - first);
            _count += count;
            _peak = 0;
            for (var index = start; index < start + count; index++)
                _peak = Math.Max(_peak, Math.Abs(samples[index]));
            _lastWrite = _time.GetTimestamp();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("Offset and count exceed the buffer length.");

        if (!Monitor.TryEnter(_gate))
        {
            // The recorder must keep its master clock even if the producer is descheduled
            // while holding this gate. Drop the skipped interval on the next queue access.
            Interlocked.Add(ref _skippedReadSamples, count);
            Array.Clear(buffer, offset, count);
            return count;
        }
        try
        {
            ApplySkippedReads();
            if (_time.GetElapsedTime(_lastWrite).TotalMilliseconds > BacklogMilliseconds)
                _head = _count = 0;
            var available = Math.Min(count, _count);
            var first = Math.Min(available, _samples.Length - _head);
            Array.Copy(_samples, _head, buffer, offset, first);
            Array.Copy(_samples, 0, buffer, offset + first, available - first);
            Array.Clear(buffer, offset + available, count - available);
            _head = (_head + available) % _samples.Length;
            _count -= available;
            _nextChannel = (_nextChannel + count) & 1;
        }
        finally { Monitor.Exit(_gate); }
        return count;
    }

    private void ApplySkippedReads()
    {
        var skipped = Interlocked.Exchange(ref _skippedReadSamples, 0);
        var drop = (int)Math.Min(skipped, _count);
        _head = (_head + drop) % _samples.Length;
        _count -= drop;
        _nextChannel = (_nextChannel + (int)(skipped & 1)) & 1;
    }
}
