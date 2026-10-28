using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using System.Collections.Immutable;
using VideoView.Core;
using VideoView.Core.Media;
using VideoView.Platform.Audio;
using VideoView.Platform.Video;

namespace VideoView.Platform.Recording;

/// <summary>A single clock drives every input. Only closed segments enter the rolling store.</summary>
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly CaptureTarget? _target;
    private readonly FfmpegTools _tools;
    private readonly List<IAudioSource> _audio = [];
    private readonly List<AudioInput> _inputs;
    private readonly List<SegmentInfo> _segments = [];
    private readonly List<double> _replayBookmarks = [];
    private readonly SemaphoreSlim _segmentGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SegmentCsvTail _csvTail = new();
    private readonly CommittedSegmentStore _segmentStore = new();
    private readonly Dictionary<string, int> _segmentPins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(ContinuousJournal Journal, SegmentInfo Segment)> _pendingPromotions = new();
    private BufferStats _bufferStats = new();
    private long _closedBytes, _evictedSegments, _diskAudits, _lastClosedIndex = -1;
    private double _lastDiskAudit = -2;
    private double _protectedFrom = -1;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly List<NamedPipeServerStream> _pipes = [];
    private readonly List<Task> _pumps = [];
    private readonly StringBuilder _diagnostics = new();
    private readonly Func<IReadOnlyList<IAudioSource>>? _testAudioFactory;
    private readonly Func<VideoFrame?>? _testVideo;
    private ScreenCaptureSource? _screen;
    public string CaptureNotice => _screen?.CaptureNotice ?? "";
    private Process? _encoder;
    private Task? _maintenance;
    private Task? _capacityMonitor;
    private Task? _stderr;
    private FileStream? _lease;
    private ContinuousJournal? _continuous;
    private string _directory = "";
    private int _width, _height;
    private bool _stopping;
    private volatile bool _continuousEnding;
    private int _startRequested, _stopRequested;
    private readonly TaskCompletionSource _startupDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _archiveTasks = [];
    private int _faulted;
    private long _lastSequence = -1, _duplicateFrames, _lateFrames;
    private double _protectedUntil = -1;
    private double _replayStartedAt;
    private string _videoEncoder = "libx264";

    public RecordingSession(AppSettings settings, CaptureTarget? target, FfmpegTools tools,
        Func<IReadOnlyList<IAudioSource>>? audioFactory = null, Func<VideoFrame?>? videoFrames = null)
    {
        settings.Validate();
        // Settings and gain values cannot change under an active recording.
        _settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        _inputs = _settings.AudioInputs.Where(a => a.Enabled).ToList();
        _target = target;
        _tools = tools;
        _bufferStats = new BufferStats { LimitBytes = _settings.BufferLimitBytes };
        _testAudioFactory = audioFactory;
        _testVideo = videoFrames;
    }

    public bool IsRunning { get; private set; }
    public bool IsRecording => _continuous != null;
    public bool IsBuffering { get; private set; }
    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
    public double BufferedSeconds { get; private set; }
    public BufferStats BufferStats => Volatile.Read(ref _bufferStats);
    public int BookmarkCount => _continuous?.Bookmarks.Count ?? _replayBookmarks.Count;
    public long RepeatedFrames => Interlocked.Read(ref _duplicateFrames);
    public long LateFrames => Interlocked.Read(ref _lateFrames);
    public string EncoderName => _videoEncoder;
    public VideoFrame? PreviewFrame => _testVideo?.Invoke() ?? _screen?.LatestFrame;
    public IReadOnlyList<(string Name, float Peak)> AudioLevels => _audio.Select(a => (a.Name, a.Peak)).ToArray();
    public event Action<string>? Status;
    public event Action<string>? Faulted;
    public event Action<RecordingManifest>? Saved;

    public async Task StartAsync(bool continuous, bool replay, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _startRequested, 1) != 0)
            throw new InvalidOperationException("이미 시작하거나 종료한 녹화 세션.");
        if (Volatile.Read(ref _stopRequested) != 0)
        { _startupDone.TrySetResult(); throw new InvalidOperationException("이미 종료 요청된 녹화 세션."); }
        Exception? failure = null;
        using var startupToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await StartCoreAsync(continuous, replay, startupToken.Token); }
        catch (Exception error) { failure = error; }
        finally { _startupDone.TrySetResult(); }
        if (failure != null)
        {
            try { await StopAsync(); } catch { }
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task StartCoreAsync(bool continuous, bool replay, CancellationToken cancellationToken)
    {
        if (IsRunning || _stopping) throw new InvalidOperationException("이미 시작한 녹화 세션.");
        if (!continuous && !replay) throw new ArgumentException("저장 방식을 하나 이상 선택해야 함.");
        if (_settings.Mode == CaptureMode.AudioOnly && _inputs.Count == 0) throw new ArgumentException("녹음할 음원을 선택해 주세요.");
        if (_inputs.Count > 8) throw new ArgumentException("동시 음원은 최대 8개.");
        await _tools.CheckAsync(cancellationToken);
        _directory = Path.Combine(_settings.BufferDirectory, _sessionId);
        CleanupAbandonedBuffers(_settings.BufferDirectory, _settings.OutputDirectory);
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_settings.OutputDirectory);
        EnsureDiskSpace(_directory);
        _lease = new FileStream(Path.Combine(_directory, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(Path.Combine(_directory, "videoview-buffer.json"), JsonSerializer.Serialize(new { Version = 1, SessionId = _sessionId }));
        try
        {
            if (_settings.Mode == CaptureMode.ScreenAndAudio)
            {
                if (_testVideo == null)
                {
                    if (_target == null) throw new ArgumentException("녹화할 창 또는 모니터를 선택해 주세요.");
                _screen = new ScreenCaptureSource(_target, _settings.FramesPerSecond,
                    _settings.HideCaptureBorder, _settings.CaptureCursor);
                    _screen.Faulted += SignalFault;
                    _screen.Start();
                }
                var deadline = Stopwatch.StartNew();
                while (PreviewFrame == null && deadline.Elapsed < TimeSpan.FromSeconds(8))
                    await Task.Delay(40, cancellationToken);
                var frame = PreviewFrame ?? throw new InvalidOperationException("화면을 가져오지 못했음. 창을 복원하거나 모니터 캡처를 직접 선택해 주세요.");
                (_width, _height) = (frame.Width, frame.Height);
                _videoEncoder = await SelectEncoderAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _audio.AddRange(_testAudioFactory?.Invoke() ?? _inputs.Select(input => AudioSourceFactory.Create(
                new AudioSourceDescriptor(input.Id, input.Name, (AudioSourceKind)input.Kind, input.ProcessId))));
            if (_audio.Count != _inputs.Count) throw new InvalidOperationException("음원 수 불일치.");
            foreach (var source in _audio) { cancellationToken.ThrowIfCancellationRequested(); source.Faulted += SignalFault; source.Start(); }
            cancellationToken.ThrowIfCancellationRequested();
            if (continuous) BeginContinuous(0);
            IsBuffering = replay;
            _replayStartedAt = 0;
            var args = BuildArguments();
            _encoder = new Process { StartInfo = new ProcessStartInfo(_tools.FfmpegPath)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true } };
            foreach (var arg in args) _encoder.StartInfo.ArgumentList.Add(arg);
            _encoder.Start();
            _stderr = ConsumeDiagnosticsAsync(_encoder);
            _clock.Start();
            var pipeIndex = 0;
            if (_settings.Mode == CaptureMode.ScreenAndAudio)
            {
                var videoPipe = _pipes[pipeIndex++];
                _pumps.Add(Task.Run(() => PumpVideoAsync(videoPipe)));
            }
            if (_audio.Count > 0)
            {
                var audioPipe = _pipes[pipeIndex];
                _pumps.Add(Task.Run(() => PumpAudioAsync(audioPipe)));
            }
            _maintenance = Task.Run(MaintainAsync);
            _capacityMonitor = Task.Run(MonitorCapacityAsync);
            // Wait for FFmpeg to consume inputs, not merely for a process to exist.
            var startup = Stopwatch.StartNew();
            while (!_pipes.All(p => p.IsConnected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_encoder.HasExited || Volatile.Read(ref _faulted) != 0) throw new InvalidOperationException("녹화 엔진 시작 실패. " + Diagnostics());
                if (startup.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("녹화 엔진 연결 시간 초과.");
                await Task.Delay(50, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            Status?.Invoke($"기록 중 · {(_settings.Mode == CaptureMode.AudioOnly ? "음성 전용" : _videoEncoder)}");
        }
        catch
        {
            _lifetime.Cancel();
            throw;
        }
    }

    private List<string> BuildArguments()
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-filter_complex_threads", "1" };
        if (_settings.Mode == CaptureMode.ScreenAndAudio)
        {
            var pipe = AddPipe("video");
            args.AddRange(["-thread_queue_size", "8", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{_width}x{_height}",
                "-framerate", _settings.FramesPerSecond.ToString(), "-probesize", "32", "-analyzeduration", "0", "-i", pipe]);
        }
        if (_audio.Count > 0)
        {
            var pipe = AddPipe("audio");
            args.AddRange(["-thread_queue_size", "128", "-f", "f32le", "-ar", "48000", "-ac", (_audio.Count * 2).ToString(),
                "-probesize", "32", "-analyzeduration", "0", "-i", pipe]);
            var source = _settings.Mode == CaptureMode.ScreenAndAudio ? 1 : 0;
            var filter = new StringBuilder($"[{source}:a]asplit={_audio.Count}");
            for (var i = 0; i < _audio.Count; i++) filter.Append($"[s{i}]");
            for (var i = 0; i < _audio.Count; i++)
                filter.Append($";[s{i}]pan=stereo|c0=c{i * 2}|c1=c{i * 2 + 1}[a{i}]");
            args.AddRange(["-filter_complex", filter.ToString()]);
        }
        if (_settings.Mode == CaptureMode.ScreenAndAudio)
        {
            args.AddRange(["-map", "0:v:0", "-vf", $"scale=-2:{_settings.Height}:force_original_aspect_ratio=decrease,pad=ceil(iw/2)*2:ceil(ih/2)*2,setsar=1",
                "-c:v", _videoEncoder, "-pix_fmt", "yuv420p", "-g", (_settings.FramesPerSecond * 2).ToString(),
                "-force_key_frames", "expr:gte(t,n_forced*2)", "-bf", "0"]);
            var bitrate = _settings.Quality switch { Quality.Low => 4000, Quality.High => 16000, _ => 8000 };
            if (_settings.FramesPerSecond == 60) bitrate = bitrate * 3 / 2;
            if (_settings.Height == 720) bitrate = bitrate * 2 / 3;
            args.AddRange(["-b:v", bitrate + "k", "-maxrate", bitrate + "k", "-bufsize", (bitrate * 2) + "k"]);
            if (_videoEncoder == "libx264") args.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-sc_threshold", "0"]);
            if (_videoEncoder == "h264_nvenc") args.AddRange(["-preset", "p2", "-tune", "ll"]);
        }
        for (var i = 0; i < _audio.Count; i++) args.AddRange(["-map", $"[a{i}]", $"-metadata:s:a:{i}", "title=" + _audio[i].Name]);
        if (_audio.Count > 0) args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000"]);
        args.AddRange(["-max_muxing_queue_size", "1024", "-f", "segment", "-segment_time", "2", "-reset_timestamps", "1",
            "-segment_format", "matroska", "-segment_list_type", "csv", "-segment_list", Path.Combine(_directory, "segments.csv"),
            Path.Combine(_directory, "part_%08d.mkv")]);
        return args;
    }

    private string AddPipe(string suffix)
    {
        var name = $"VideoView_{_sessionId}_{suffix}";
        _pipes.Add(new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 1024 * 1024));
        return @"\\.\pipe\" + name;
    }

    private async Task PumpVideoAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await pipe.WaitForConnectionAsync(_lifetime.Token);
            long written = 0;
            VideoFrame? previousFrame = null;
            var canvas = new FrameCanvas(_width, _height);
            while (!_lifetime.IsCancellationRequested)
            {
                await PaceAsync((double)written / _settings.FramesPerSecond);
                var current = PreviewFrame ?? throw new IOException("화면 캡처 중단.");
                // Old timeline slots keep the historical image; never backdate a fresh frame.
                var overdue = ElapsedSeconds - (double)written / _settings.FramesPerSecond > 1.5 / _settings.FramesPerSecond;
                if (overdue) Interlocked.Increment(ref _lateFrames);
                var frame = overdue && previousFrame != null ? previousFrame : current;
                if (!overdue || previousFrame == null) previousFrame = current;
                if (frame.Sequence == _lastSequence) Interlocked.Increment(ref _duplicateFrames);
                _lastSequence = frame.Sequence;
                await pipe.WriteAsync(canvas.Fit(frame), _lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(8), _lifetime.Token);
                written++;
                if (ElapsedSeconds - (double)written / _settings.FramesPerSecond > 3) throw new IOException("인코더가 실시간 속도를 따라가지 못함. 화질·프레임을 낮춰 주세요.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { if (!_stopping) SignalFault(e.Message); }
        finally { pipe.Dispose(); }
    }

    private async Task PumpAudioAsync(NamedPipeServerStream pipe)
    {
        const int frames = 480; // 10 ms, identical timeline for every audio source.
        var samples = new float[frames * 2];
        var interleaved = new float[frames * 2 * _audio.Count];
        var bytes = new byte[interleaved.Length * sizeof(float)];
        try
        {
            await pipe.WaitForConnectionAsync(_lifetime.Token);
            long writtenFrames = 0;
            while (!_lifetime.IsCancellationRequested)
            {
                await PaceAsync((double)writtenFrames / 48000);
                for (var input = 0; input < _audio.Count; input++)
                {
                    // A missed interval is silence. Reading fresh samples into that interval would desync it.
                    if (ElapsedSeconds - (double)writtenFrames / 48000 > 0.04) Array.Clear(samples);
                    else _audio[input].Read(samples, 0, samples.Length);
                    var gain = _inputs[input].Gain;
                    for (var sample = 0; sample < frames; sample++)
                    {
                        interleaved[sample * _audio.Count * 2 + input * 2] = Math.Clamp(samples[sample * 2] * gain, -1, 1);
                        interleaved[sample * _audio.Count * 2 + input * 2 + 1] = Math.Clamp(samples[sample * 2 + 1] * gain, -1, 1);
                    }
                }
                Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
                await pipe.WriteAsync(bytes, _lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(8), _lifetime.Token);
                writtenFrames += frames;
                if (ElapsedSeconds - (double)writtenFrames / 48000 > 3) throw new IOException("오디오 저장이 지연됨. 저장 장치 상태를 확인해 주세요.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { if (!_stopping) SignalFault(e.Message); }
        finally { pipe.Dispose(); }
    }

    private async Task PaceAsync(double seconds)
    {
        var wait = seconds - ElapsedSeconds;
        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), _lifetime.Token);
    }

    private async Task MaintainAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(200, _lifetime.Token);
                await ReadSegmentsAsync();
                if (_encoder is { HasExited: true }) throw new IOException("녹화 엔진이 종료됨. " + Diagnostics());
                EnsureDiskSpace(_directory);
                if (IsRecording) EnsureDiskSpace(_settings.OutputDirectory);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { if (!_stopping) SignalFault(e.Message); }
    }

    private async Task MonitorCapacityAsync()
    {
        try
        {
            // The promotion worker can be waiting on a slow destination drive. Never put
            // the safety guard behind either its read gate or its journal/state lock.
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(1000, _lifetime.Token);
                var occupied = MeasureRingBytes();
                Interlocked.Increment(ref _diskAudits);
                ThrowIfBufferOverCapacity(occupied);
                EnsureDiskSpace(_directory);
                EnsureDiskSpace(_settings.OutputDirectory);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_stopping) SignalFault(error.Message); }
    }

    private long MeasureRingBytes()
    {
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "part_*.mkv"))
        {
            try { bytes = checked(bytes + new FileInfo(path).Length); }
            catch (FileNotFoundException) { } // A closed ring entry expired during the scan.
        }
        return bytes;
    }

    private void ThrowIfBufferOverCapacity(long occupied)
    {
        const long reserve = 64L * 1024 * 1024;
        if (occupied <= _settings.BufferLimitBytes + reserve) return;
        throw new IOException($"임시 저장 한도 초과: {occupied / (1024d * 1024):F1}MiB / " +
            $"{_settings.BufferLimitBytes / (1024d * 1024):F0}MiB (진행 중 조각 여유 64MiB). " +
            "저장 요청한 조각을 보존하고 기록을 중단합니다. 용량을 늘리거나 화질을 낮춰 주세요.");
    }

    private async Task ReadSegmentsAsync()
    {
        // Only one reader advances the CSV cursor. Large promotions never hold the state lock.
        await _readGate.WaitAsync();
        var maintenance = Stopwatch.StartNew();
        var lockMilliseconds = 0d;
        try
        {
            var incoming = await _csvTail.ReadAsync(_directory);
            await _segmentGate.WaitAsync();
            var held = Stopwatch.StartNew();
            try
            {
                foreach (var segment in incoming)
                {
                    var index = long.Parse(Path.GetFileName(segment.Path).AsSpan(5, 8), CultureInfo.InvariantCulture);
                    if (index <= _lastClosedIndex) throw new InvalidDataException("녹화 조각 순서가 바뀌었습니다.");
                    _lastClosedIndex = index;
                    _segments.Add(segment);
                    _closedBytes += segment.Bytes;
                    if (_continuous != null && segment.EndSeconds > _continuous.StartSeconds)
                    {
                        PinSegment(segment.Path);
                        _pendingPromotions.Enqueue((_continuous, segment));
                    }
                }
            }
            finally { lockMilliseconds += held.Elapsed.TotalMilliseconds; _segmentGate.Release(); }
            while (_pendingPromotions.TryPeek(out var pending))
            {
                var (journal, segment) = pending;
                var destination = Path.Combine(journal.Directory, Path.GetFileName(segment.Path));
                // The CSV cursor may already be ahead. Keep each uncommitted promotion in this
                // queue and pinned until both its file and journal commit, including final drain.
                await _segmentStore.PromoteAsync(segment.Path, destination);
                await _segmentGate.WaitAsync();
                held.Restart();
                try
                {
                    var committed = segment with { Path = destination };
                    journal.Segments.Add(committed);
                    try { WriteJournal(journal); }
                    catch { journal.Segments.Remove(committed); throw; }
                    UnpinSegment(segment.Path);
                    _pendingPromotions.Dequeue();
                }
                finally { lockMilliseconds += held.Elapsed.TotalMilliseconds; _segmentGate.Release(); }
            }
            await _segmentGate.WaitAsync();
            held.Restart();
            try
            {
                var latest = _segments.LastOrDefault()?.EndSeconds ?? 0;
                _replayBookmarks.RemoveAll(p => p < Math.Max(_replayStartedAt, latest - _settings.ReplaySeconds));
                var keepSeconds = IsBuffering ? _settings.ReplaySeconds + 2 : 2;
                var pinned = _segments.Where(IsPinned).Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var expired in ReplayWindow.Expired(_segments, latest, keepSeconds, _settings.BufferLimitBytes, pinned))
                {
                    File.Delete(expired.Path); // Removing a ring link never changes a promoted source.
                    _segments.Remove(expired);
                    _closedBytes -= expired.Bytes;
                    _evictedSegments++;
                }
                BufferedSeconds = IsBuffering && _segments.Count > 0
                    ? Math.Min(_settings.ReplaySeconds, Math.Max(0, latest - Math.Max(_replayStartedAt, _segments[0].StartSeconds))) : 0;
                var openPath = Path.Combine(_directory, $"part_{_lastClosedIndex + 1:00000000}.mkv");
                var occupied = _closedBytes + (File.Exists(openPath) ? new FileInfo(openPath).Length : 0);
                // Periodic full audit catches writer backlog or an unexpectedly named chunk.
                // Ordinary 200ms polls stat only the open segment, not every retained file.
                if (ElapsedSeconds - _lastDiskAudit >= 2 || _stopping)
                {
                    occupied = MeasureRingBytes();
                    _lastDiskAudit = ElapsedSeconds;
                    Interlocked.Increment(ref _diskAudits);
                }
                lockMilliseconds += held.Elapsed.TotalMilliseconds;
                PublishBufferStats(occupied, maintenance.Elapsed.TotalMilliseconds, lockMilliseconds);
                ThrowIfBufferOverCapacity(occupied);
            }
            finally { _segmentGate.Release(); }
        }
        finally { _readGate.Release(); }
    }

    private bool IsPinned(SegmentInfo segment) => _segmentPins.ContainsKey(segment.Path) ||
        (_protectedUntil >= 0 && segment.EndSeconds > _protectedFrom && segment.StartSeconds < _protectedUntil);

    private void PinSegment(string path) => _segmentPins[path] = _segmentPins.GetValueOrDefault(path) + 1;
    private void UnpinSegment(string path)
    {
        if (_segmentPins[path] == 1) _segmentPins.Remove(path);
        else _segmentPins[path]--;
    }

    private void PublishBufferStats(long occupied, double milliseconds, double lockMilliseconds)
    {
        var previous = BufferStats;
        Volatile.Write(ref _bufferStats, new BufferStats
        {
            BufferBytes = occupied, LimitBytes = _settings.BufferLimitBytes, ClosedBytes = _closedBytes,
            ClosedSegments = _segments.Count, PinnedBytes = _segments.Where(IsPinned).Sum(s => s.Bytes),
            RetainedSeconds = BufferedSeconds, MaintenanceMilliseconds = milliseconds,
            MaxMaintenanceMilliseconds = Math.Max(previous.MaxMaintenanceMilliseconds, milliseconds),
            SegmentLockMilliseconds = lockMilliseconds,
            MaxSegmentLockMilliseconds = Math.Max(previous.MaxSegmentLockMilliseconds, lockMilliseconds),
            MaintenanceCount = previous.MaintenanceCount + 1, CsvBytesRead = _csvTail.BytesRead,
            CsvRowsRead = _csvTail.RowsRead, DiskAuditCount = _diskAudits, EvictedSegments = _evictedSegments,
            HardLinkCount = _segmentStore.HardLinkCount, CopyCount = _segmentStore.CopyCount,
            CopiedBytes = _segmentStore.CopiedBytes, SampledAt = DateTimeOffset.UtcNow
        });
    }

    public async Task SetContinuousAsync(bool enabled)
    {
        ContinuousSnapshot? toFinalize = null;
        double finalEnd = 0;
        Task? archive = null;
        await _operationGate.WaitAsync();
        try
        {
            if (!IsRunning || _stopping || Volatile.Read(ref _faulted) != 0)
                throw new InvalidOperationException("기록 세션이 실행 중이 아니거나 복구 처리가 필요합니다.");
            if (enabled == IsRecording) return;
            if (enabled)
            {
                await _segmentGate.WaitAsync();
                try { BeginContinuous(ElapsedSeconds); }
                finally { _segmentGate.Release(); }
                Status?.Invoke("연속 녹화 시작");
            }
            else
            {
                _continuousEnding = true;
                var end = ElapsedSeconds;
                await WaitForClosedSegmentAsync(end, CancellationToken.None, retainReadGate: true);
                try
                {
                    // Boundary verification hands off the reader gate. No maintenance worker
                    // can capture this journal or start another promotion before it is detached.
                    await _segmentGate.WaitAsync();
                    try
                    {
                        var journal = _continuous;
                        if (journal != null)
                        {
                            journal.EndSeconds = end;
                            WriteJournal(journal);
                            toFinalize = SnapshotContinuous(journal);
                        }
                        _continuous = null;
                    }
                    finally { _segmentGate.Release(); }
                }
                finally { _readGate.Release(); }
                finalEnd = end;
                if (toFinalize != null)
                {
                    archive = FinalizeContinuousAsync(toFinalize, finalEnd, CancellationToken.None);
                    _archiveTasks.Add(archive);
                }
            }
        }
        finally { _continuousEnding = false; _operationGate.Release(); }
        if (archive != null) await archive;
    }

    public async Task SetReplayAsync(bool enabled)
    {
        await _segmentGate.WaitAsync();
        try
        {
            if (!IsRunning || _stopping) throw new InvalidOperationException("기록 세션이 실행 중이 아님.");
            if (enabled && !IsBuffering) { _replayStartedAt = ElapsedSeconds; _replayBookmarks.Clear(); }
            IsBuffering = enabled;
            if (!enabled) { BufferedSeconds = 0; _replayBookmarks.Clear(); }
        }
        finally { _segmentGate.Release(); }
    }

    public async Task<double> AddBookmarkAsync()
    {
        var position = ElapsedSeconds;
        await _segmentGate.WaitAsync();
        try
        {
            if (!IsRunning || _stopping || (!IsRecording && !IsBuffering))
                throw new InvalidOperationException("먼저 연속 녹화 또는 최근 구간 보관을 켜 주세요.");
            if (_continuousEnding)
                throw new InvalidOperationException("연속 녹화 종료 처리 중입니다. 완료 후 다시 표시해 주세요.");
            var journal = _continuous;
            if (journal != null && position < journal.StartSeconds)
                throw new InvalidOperationException("녹화 전환 중입니다. 다시 표시해 주세요.");
            _replayBookmarks.RemoveAll(p => p < Math.Max(_replayStartedAt, position - _settings.ReplaySeconds));
            if (journal?.Bookmarks.Count >= 500 || (IsBuffering && _replayBookmarks.Count >= 500))
                throw new InvalidOperationException("기록 하나에 중요 지점을 최대 500개 표시할 수 있습니다.");
            if (journal != null)
            {
                journal.Bookmarks.Add(position);
                try { WriteJournal(journal); }
                catch { journal.Bookmarks.Remove(position); throw; }
            }
            if (IsBuffering) _replayBookmarks.Add(position);
            return journal != null ? position - journal.StartSeconds : position;
        }
        finally { _segmentGate.Release(); }
    }

    public async Task<RecordingManifest> SaveReplayAsync(CancellationToken cancellationToken = default)
    {
        // Serialize exports, but preserve the keypress time before waiting for the previous save.
        var end = ElapsedSeconds;
        var start = Math.Max(_replayStartedAt, end - _settings.ReplaySeconds);
        if (!IsRunning || _stopping || !IsBuffering || end - start < 0.25) throw new InvalidOperationException("최근 구간이 아직 준비되지 않았음.");
        if (!await _saveGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("직전 구간을 저장 중임. 완료 후 다시 눌러 주세요.");
        string? snapshot = null;
        var published = false;
        var promoted = false;
        List<SegmentInfo> pinned = [];
        try
        {
            await _segmentGate.WaitAsync(cancellationToken);
            try { _protectedFrom = start; _protectedUntil = end; }
            finally { _segmentGate.Release(); }
            Status?.Invoke("최근 구간 저장 중… 기록은 계속됨");
            await WaitForClosedSegmentAsync(end, cancellationToken);
            List<SegmentInfo> selected;
            List<double> bookmarks;
            ContinuousJournal pending;
            await _segmentGate.WaitAsync(cancellationToken);
            try
            {
                selected = ReplayWindow.Select(_segments, end, end - start).ToList();
                if (selected.Count == 0) throw new IOException("저장할 녹화 조각이 없음.");
                // A save request promotes chunks out of the disposable ring into recoverable storage.
                snapshot = Path.Combine(_settings.OutputDirectory, ".sessions", Guid.NewGuid().ToString("N"));
                pending = new ContinuousJournal { Directory = snapshot, BufferDirectory = _directory,
                    StartSeconds = Math.Max(start, selected[0].StartSeconds), EndSeconds = end,
                    HasVideo = _settings.Mode == CaptureMode.ScreenAndAudio,
                    Tracks = _inputs.Select((a, i) => new TrackInfo(i, a.Name)).ToList() };
                pending.Bookmarks = _replayBookmarks.Where(p => p >= pending.StartSeconds && p < end).ToList();
                bookmarks = pending.Bookmarks;
                foreach (var segment in selected)
                {
                    PinSegment(segment.Path);
                    pinned.Add(segment);
                }
                _protectedUntil = -1;
                _protectedFrom = -1;
            }
            finally { _segmentGate.Release(); }
            Directory.CreateDirectory(snapshot);
            WriteJournal(pending);
            foreach (var segment in selected)
            {
                var destination = Path.Combine(snapshot, Path.GetFileName(segment.Path));
                // Finish promoting a requested save even if export is cancelled. An incomplete
                // promotion would otherwise lose recovery sources when the ring expires.
                await _segmentStore.PromoteAsync(segment.Path, destination);
                pending.Segments.Add(segment with { Path = destination });
                WriteJournal(pending);
            }
            promoted = true;
            await _segmentGate.WaitAsync();
            try { foreach (var segment in pinned) UnpinSegment(segment.Path); pinned.Clear(); }
            finally { _segmentGate.Release(); }
            selected = pending.Segments;
            start = Math.Max(start, selected[0].StartSeconds);
            var result = await AssembleAsync(selected, start, end, "리플레이", cancellationToken, bookmarks);
            published = true;
            Saved?.Invoke(result);
            Status?.Invoke($"최근 {result.DurationSeconds:F1}초 저장 완료");
            return result;
        }
        finally
        {
            // If promotion failed, stop before unprotected retention can delete recovery input.
            if (snapshot != null && !promoted)
            {
                try { SignalFault("최근 구간 보존 실패. 복구용 원본 조각을 보존하고 기록을 중단합니다."); }
                finally
                {
                    // A normal stop can race this failure. SignalFault ignores late capture
                    // callbacks during stopping, but Dispose must still retain replay sources.
                    Interlocked.Exchange(ref _faulted, 1);
                }
            }
            await _segmentGate.WaitAsync();
            try { _protectedUntil = -1; _protectedFrom = -1; }
            finally { _segmentGate.Release(); }
            try { if (published && snapshot != null) DeleteOwnedFolder(snapshot, Path.Combine(_settings.OutputDirectory, ".sessions")); }
            catch (IOException error) { Status?.Invoke("파일은 저장됐지만 임시 조각 정리가 필요함. " + error.Message); }
            finally { _saveGate.Release(); }
        }
    }

    private async Task WaitForClosedSegmentAsync(double end, CancellationToken token, bool retainReadGate = false)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            await ReadSegmentsAsync();
            if (await VerifyClosedBoundaryAsync(end, retainReadGate, token)) return;
            if (_stopping)
            {
                await _drained.Task.WaitAsync(token);
                if (await VerifyClosedBoundaryAsync(end - 0.1, retainReadGate, token)) return;
                throw new IOException("중단 전에 확보된 구간이 부족함. 복구용 조각은 보존됩니다.");
            }
            if (_encoder is { HasExited: true }) throw new IOException("녹화 엔진이 종료되어 요청 구간을 완성하지 못했음.");
            if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("녹화 조각 저장이 지연되고 있음.");
            await Task.Delay(100, token);
        }
    }

    private async Task<bool> VerifyClosedBoundaryAsync(double end, bool retainReadGate, CancellationToken token)
    {
        await _readGate.WaitAsync(token);
        var handedOff = false;
        try
        {
            await _segmentGate.WaitAsync(token);
            try
            {
                if ((_segments.LastOrDefault()?.EndSeconds ?? 0) < end) return false;
                handedOff = retainReadGate;
                return true;
            }
            finally { _segmentGate.Release(); }
        }
        finally { if (!handedOff) _readGate.Release(); }
    }

    private ContinuousSnapshot SnapshotContinuous(ContinuousJournal journal)
    {
        if (_pendingPromotions.Any(p => ReferenceEquals(p.Journal, journal)))
            throw new IOException("보존하지 못한 녹화 조각이 남아 있습니다. 원본과 복구 정보를 보존합니다.");
        return new(journal.Directory, journal.StartSeconds, journal.Segments.ToImmutableArray(), journal.Bookmarks.ToImmutableArray());
    }

    private sealed record ContinuousSnapshot(string Directory, double StartSeconds,
        ImmutableArray<SegmentInfo> Segments, ImmutableArray<double> Bookmarks);

    private void BeginContinuous(double start)
    {
        var folder = Path.Combine(_settings.OutputDirectory, ".sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        _continuous = new ContinuousJournal { Directory = folder, StartSeconds = start, HasVideo = _settings.Mode == CaptureMode.ScreenAndAudio,
            Tracks = _inputs.Select((a, i) => new TrackInfo(i, a.Name)).ToList(), BufferDirectory = _directory };
        WriteJournal(_continuous);
    }

    private static void WriteJournal(ContinuousJournal journal)
    {
        var path = Path.Combine(journal.Directory, "session.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(journal));
        File.Move(path + ".tmp", path, true);
    }

    private async Task<RecordingManifest> AssembleAsync(IReadOnlyList<SegmentInfo> segments, double start, double end, string title, CancellationToken token,
        IReadOnlyList<double>? bookmarks = null)
    {
        var hasVideo = _settings.Mode == CaptureMode.ScreenAndAudio;
        var date = DateTimeOffset.Now;
        var folder = Path.Combine(_settings.OutputDirectory, date.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{date:HHmmss}_{Guid.NewGuid():N}" + (hasVideo ? ".mkv" : ".mka"));
        var joined = path + ".join.mkv";
        try
        {
            await _tools.ConcatenateSegmentsAsync(segments, joined, hasVideo, token);
            var info = await _tools.ProbeAsync(joined, token);
            var offset = Math.Max(0, start - segments[0].StartSeconds);
            var duration = Math.Min(end - start, info.DurationSeconds - offset);
            if (duration <= 0) throw new InvalidDataException("저장할 구간이 비어 있음.");
            // Full continuous recordings need no lossy second encode; only clip boundaries do.
            if (offset < 0.001 && Math.Abs(duration - info.DurationSeconds) < 0.08)
                File.Move(joined, path, false);
            else if (offset < 0.001)
            {
                var partial = path + ".partial";
                try
                {
                    await _tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-n", "-i", joined,
                        "-t", duration.ToString("G17", CultureInfo.InvariantCulture), "-map", "0", "-c", "copy", "-f", "matroska", partial], token);
                    File.Move(partial, path, false);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            else await _tools.TrimArchiveAsync(joined, path, offset, duration, hasVideo, token);
            var actual = await _tools.ProbeAsync(path, token);
            var manifest = new RecordingManifest { Title = $"{title} {date:MM/dd HH:mm:ss}", CreatedAt = date, MediaPath = path,
                DurationSeconds = actual.DurationSeconds, HasVideo = hasVideo, Tracks = actual.Tracks.ToList() };
            var marks = BookmarkTimeline.Map(segments, start, end, actual.DurationSeconds, bookmarks);
            if (marks.Count > 0) manifest.Edits = new RecordingEdits { End = actual.DurationSeconds,
                Position = marks[0], Bookmarks = marks }.Normalize(manifest);
            SaveSidecar(manifest);
            return manifest;
        }
        finally { if (File.Exists(joined)) File.Delete(joined); }
    }

    private async Task FinalizeContinuousAsync(ContinuousSnapshot journal, double end, CancellationToken token)
    {
        if (journal.Segments.Length == 0)
        {
            if (Volatile.Read(ref _faulted) == 0) DeleteOwnedFolder(journal.Directory, Path.Combine(_settings.OutputDirectory, ".sessions"));
            Status?.Invoke("녹화가 너무 짧아 저장할 구간이 없음."); return;
        }
        Status?.Invoke("연속 녹화 파일 정리 중…");
        var recording = await AssembleAsync(journal.Segments, journal.StartSeconds, Math.Min(end, journal.Segments[^1].EndSeconds), "녹화", token, journal.Bookmarks);
        Saved?.Invoke(recording);
        DeleteOwnedFolder(journal.Directory, Path.Combine(_settings.OutputDirectory, ".sessions"));
        Status?.Invoke($"녹화 저장 완료 · {TimeSpan.FromSeconds(recording.DurationSeconds):hh\\:mm\\:ss}");
    }

    public Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
        {
            _stopping = true;
            _lifetime.Cancel();
            if (Volatile.Read(ref _startRequested) == 0) _startupDone.TrySetResult();
            _ = Task.Run(StopCoreAsync);
        }
        return _stopped.Task;
    }

    private async Task StopCoreAsync()
    {
        Exception? failure = null;
        var end = ElapsedSeconds;
        try
        {
            await _startupDone.Task;
            foreach (var source in _audio) { try { source.Stop(); } catch (Exception error) { failure ??= error; } }
            try { _screen?.Stop(); } catch (Exception error) { failure ??= error; }
            try { await Task.WhenAll(_pumps).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { failure ??= error; }
            foreach (var pipe in _pipes) pipe.Dispose();
            if (_encoder != null)
            {
                try
                {
                    await _encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                    if (_encoder.ExitCode != 0) failure ??= new IOException("인코더 비정상 종료. " + Diagnostics());
                }
                catch (Exception error)
                {
                    failure ??= error;
                    try { _encoder.Kill(true); await _encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                }
            }
            if (_maintenance != null) { try { await _maintenance; } catch { } }
            if (_capacityMonitor != null) { try { await _capacityMonitor; } catch { } }
            if (_stderr != null) { try { await _stderr.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
            _clock.Stop();
            IsRunning = false;
            IsBuffering = false;
            try { if (_directory.Length > 0) await ReadSegmentsAsync(); } catch (Exception error) { failure ??= error; }
            _drained.TrySetResult();
            await _operationGate.WaitAsync();
            ContinuousSnapshot? continuous = null;
            try
            {
                await _segmentGate.WaitAsync();
                try
                {
                    var journal = _continuous;
                    if (journal != null)
                    {
                        journal.EndSeconds = end;
                        try { WriteJournal(journal); continuous = SnapshotContinuous(journal); }
                        catch (Exception error) { failure ??= error; }
                    }
                    _continuous = null;
                }
                finally { _segmentGate.Release(); }
            }
            finally { _operationGate.Release(); }
            if (Volatile.Read(ref _faulted) != 0)
                failure ??= new IOException("기록 도중 오류가 발생했습니다. 불완전한 원본을 완료로 표시하지 않고 복구 정보를 보존합니다.");
            if (failure == null && continuous != null) await FinalizeContinuousAsync(continuous, end, CancellationToken.None);
            try { await Task.WhenAll(_archiveTasks.ToArray()); } catch (Exception error) { failure ??= error; }
            if (failure != null) throw new IOException("정상 종료하지 못해 복구용 조각을 보존했습니다. " + failure.Message, failure);
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _faulted, 1);
            failure = error;
        }
        finally
        {
            IsRunning = false; IsBuffering = false; _clock.Stop();
            _drained.TrySetResult();
            // Recovery must not acquire the buffer lease while a replay still promotes files
            // or writes its journal. Draining first lets any save waiting for the last chunk exit.
            await _saveGate.WaitAsync();
            try { _lease?.Dispose(); _lease = null; }
            finally { _saveGate.Release(); }
        }
        if (failure == null) _stopped.TrySetResult();
        else _stopped.TrySetException(failure);
    }

    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        try { await StopAsync(); } catch (Exception error) { failure = error; }
        await _saveGate.WaitAsync();
        try
        {
            foreach (var source in _audio) source.Dispose();
            _screen?.Dispose();
            _encoder?.Dispose();
            _lifetime.Dispose();
            if (_directory.Length > 0 && _continuous == null && Volatile.Read(ref _faulted) == 0) DeleteOwnedFolder(_directory, _settings.BufferDirectory);
        }
        finally { _saveGate.Release(); }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void SignalFault(string message)
    {
        if (_stopping || Interlocked.Exchange(ref _faulted, 1) != 0) return;
        _ = Task.Run(async () => { try { await StopAsync(); } catch (Exception e) { Faulted?.Invoke("복구용 녹화 조각 보존됨. " + e.Message); } });
        if (Faulted is { } handlers)
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(message); } catch (Exception error) { Trace.TraceError(error.ToString()); }
    }

    private async Task ConsumeDiagnosticsAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
            lock (_diagnostics) { _diagnostics.AppendLine(line); if (_diagnostics.Length > 16000) _diagnostics.Remove(0, _diagnostics.Length - 16000); }
    }
    private string Diagnostics() { lock (_diagnostics) return _diagnostics.ToString(); }

    private async Task<string> SelectEncoderAsync(CancellationToken token)
    {
        foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await _tools.RunAsync(["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=size=1280x720:rate=30", "-frames:v", "1", "-c:v", encoder, "-f", "null", "-"], timeout.Token);
                return encoder;
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        return "libx264";
    }

    private static void EnsureDiskSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root != null && new DriveInfo(root).AvailableFreeSpace < 256L * 1024 * 1024)
            throw new IOException("저장 공간이 256MB 미만임. 공간을 확보한 뒤 다시 시작해 주세요.");
    }

    public static void SaveSidecar(RecordingManifest recording)
    {
        var file = recording.MediaPath + ".recording.json";
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(recording, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }

    private static void DeleteOwnedFolder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("임시 폴더 경로가 저장 범위를 벗어남.");
        if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0) Directory.Delete(full, true);
    }

    private static void CleanupAbandonedBuffers(string root, string outputRoot)
    {
        if (!Directory.Exists(root)) return;
        var protectedBuffers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var journals = Path.Combine(outputRoot, ".sessions");
        if (Directory.Exists(journals))
        {
            foreach (var path in Directory.EnumerateFiles(journals, "session.json", SearchOption.AllDirectories))
                try
                {
                    var pending = JsonSerializer.Deserialize<ContinuousJournal>(File.ReadAllText(path));
                    if (pending != null) protectedBuffers.Add(Path.GetFullPath(pending.BufferDirectory));
                }
                catch { return; } // Unknown recovery state: preserve rather than discard.
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (protectedBuffers.Contains(Path.GetFullPath(directory))) continue;
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                !File.Exists(Path.Combine(directory, "videoview-buffer.json")) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                // Acquiring the exclusive lease proves no capture still owns this disposable ring.
                using (new FileStream(Path.Combine(directory, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                DeleteOwnedFolder(directory, root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<IReadOnlyList<RecordingManifest>> RecoverAsync(AppSettings settings, FfmpegTools tools,
        CancellationToken token = default, IProgress<string>? warnings = null)
    {
        var root = Path.Combine(settings.OutputDirectory, ".sessions");
        if (!Directory.Exists(root)) return [];
        var recovered = new List<RecordingManifest>();
        foreach (var directory in Directory.EnumerateDirectories(root).ToArray())
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var journalFile = Path.Combine(directory, "session.json");
            if (!File.Exists(journalFile)) { warnings?.Report("복구 정보 없는 조각 보존: " + directory); continue; }
            try
            {
                var journal = JsonSerializer.Deserialize<ContinuousJournal>(await File.ReadAllTextAsync(journalFile, token));
                if (journal == null || !string.Equals(Path.GetFullPath(journal.Directory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("복구 경로 정보 불일치.");
                if (!double.IsFinite(journal.StartSeconds) || journal.StartSeconds < 0 ||
                    journal.EndSeconds is { } requestedEnd && (!double.IsFinite(requestedEnd) || requestedEnd <= journal.StartSeconds))
                    throw new InvalidDataException("복구 구간 정보가 올바르지 않음.");
                if (journal.PublishedRecordingPath != null && File.Exists(journal.PublishedRecordingPath))
                { warnings?.Report("이미 부분 복구한 손상 조각 보존: " + directory); continue; }
                var bufferPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.BufferDirectory)) + Path.DirectorySeparatorChar;
                var bufferAvailable = Directory.Exists(journal.BufferDirectory) && Path.GetFullPath(journal.BufferDirectory).StartsWith(bufferPrefix, StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(journal.BufferDirectory) & FileAttributes.ReparsePoint) == 0;
                FileStream? lease = null;
                if (bufferAvailable)
                {
                    try { lease = new FileStream(Path.Combine(journal.BufferDirectory, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { continue; }
                }
                using (lease)
                {
                    // Recover closed chunks that were committed by FFmpeg but not yet copied by maintenance.
                    if (bufferAvailable)
                    {
                        foreach (var buffered in ReadRecoveryCsv(journal.BufferDirectory))
                        {
                            if (buffered.EndSeconds <= journal.StartSeconds || buffered.StartSeconds >= (journal.EndSeconds ?? double.MaxValue)) continue;
                            var copy = Path.Combine(directory, Path.GetFileName(buffered.Path));
                            await new CommittedSegmentStore().PromoteAsync(buffered.Path, copy, token);
                            if (!journal.Segments.Any(s => Path.GetFileName(s.Path) == Path.GetFileName(copy))) journal.Segments.Add(buffered with { Path = copy });
                        }
                        WriteJournal(journal);
                    }
                    var damaged = false;
                    var segments = new List<SegmentInfo>();
                    foreach (var path in Directory.EnumerateFiles(directory, "part_*.mkv").Order())
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var info = await tools.ProbeAsync(path, token);
                            if (info.HasVideo != journal.HasVideo || info.Tracks.Count != journal.Tracks.Count) throw new InvalidDataException("음원 정보 불일치.");
                            var known = journal.Segments.FirstOrDefault(s => Path.GetFileName(s.Path) == Path.GetFileName(path));
                            var start = known?.StartSeconds ?? (segments.LastOrDefault()?.EndSeconds ?? journal.StartSeconds);
                            segments.Add(new(path, start, known?.EndSeconds ?? start + info.DurationSeconds, new FileInfo(path).Length));
                        }
                        catch (Exception error) when (!token.IsCancellationRequested)
                        { damaged = true; warnings?.Report("읽을 수 없는 조각 보존: " + path + " · " + error.Message); }
                    }
                    segments = segments.OrderBy(s => s.StartSeconds).ToList();
                    if (segments.Count == 0) { warnings?.Report("재생 가능한 녹화 조각이 없음: " + directory); continue; }
                    var clone = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
                    clone.Mode = journal.HasVideo ? CaptureMode.ScreenAndAudio : CaptureMode.AudioOnly;
                    var helper = new RecordingSession(clone, null, tools);
                    var result = await helper.AssembleAsync(segments, Math.Max(journal.StartSeconds, segments[0].StartSeconds), Math.Min(journal.EndSeconds ?? double.MaxValue, segments[^1].EndSeconds), "복구", token, journal.Bookmarks);
                    result.Recovered = true;
                    SaveSidecar(result);
                    recovered.Add(result);
                    journal.PublishedRecordingPath = result.MediaPath;
                    WriteJournal(journal);
                    if (!damaged) DeleteOwnedFolder(directory, root);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            { warnings?.Report("복구하지 못한 기록 보존: " + directory + " · " + error.Message); }
        }
        return recovered;
    }

    private static IEnumerable<SegmentInfo> ReadRecoveryCsv(string directory)
    {
        var file = Path.Combine(directory, "segments.csv");
        if (!File.Exists(file)) yield break;
        foreach (var line in File.ReadLines(file))
        {
            var endComma = line.LastIndexOf(',');
            var startComma = endComma > 0 ? line.LastIndexOf(',', endComma - 1) : -1;
            if (startComma < 0) continue;
            var name = Path.GetFileName(line[..startComma].Trim('"'));
            if (!name.StartsWith("part_", StringComparison.Ordinal) || !name.EndsWith(".mkv", StringComparison.Ordinal)) continue;
            var path = Path.Combine(directory, name);
            if (double.TryParse(line[(startComma + 1)..endComma], CultureInfo.InvariantCulture, out var start) &&
                double.TryParse(line[(endComma + 1)..], CultureInfo.InvariantCulture, out var end) &&
                double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && File.Exists(path))
                yield return new(path, start, end, new FileInfo(path).Length);
        }
    }

    public sealed class ContinuousJournal
    {
        public string Directory { get; set; } = "";
        public string BufferDirectory { get; set; } = "";
        public double StartSeconds { get; set; }
        public double? EndSeconds { get; set; }
        public string? PublishedRecordingPath { get; set; }
        public bool HasVideo { get; set; }
        public List<TrackInfo> Tracks { get; set; } = [];
        public List<SegmentInfo> Segments { get; set; } = [];
        public List<double> Bookmarks { get; set; } = [];
    }
}
