using System.Windows;
using VideoView.App.Controls;
using VideoView.Core;
using VideoView.Core.Media;

namespace VideoView.App;

public partial class MainWindow
{
    private PreviewCache? _previewCache;
    private PreviewSlice? _previewSlice, _prefetchedSlice;
    private CancellationTokenSource? _sliceCancellation, _prefetchCancellation;
    private long _sliceGeneration;
    private double _loadingWindow = -1;
    private double _failedPrefetchStart = -1;
    private string? _loadingMix;
    private bool _mediaReady;

    private PreviewCache PreviewCache => _previewCache ??= new(Tools(),
        Path.Combine(Path.GetDirectoryName(_store.FilePath)!, "PreviewCache-v1"));
    private ExportTrack[] CurrentMix() => _exportRows.Select(row => new ExportTrack(row.Index, row.Gain, row.Enabled)).ToArray();
    private static string MixKey(RecordingManifest recording, ExportTrack[] tracks) => recording.Id + System.Text.Json.JsonSerializer.Serialize(tracks);

    private async Task TrackEditorWork(Task work)
    {
        _editorTasks.Add(work);
        try { await work; }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing) ReportError(error); }
        finally { _editorTasks.Remove(work); }
    }
    private async Task LoadEditorAsync(RecordingManifest recording, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await RequestSliceAsync(Timeline.Position, false);
            token.ThrowIfCancellationRequested();
            // Full-file waveform decoding is independent of playback readiness.
            _ = TrackEditorWork(LoadWaveformsAsync(recording, token));
            var info = await Tools().ProbeAsync(recording.MediaPath, token);
            if (!ReferenceEquals(_selected, recording) || _closing) return;
            _editorFrameRate = info.FramesPerSecond > 0 ? info.FramesPerSecond : 30;
            if (info.HasVideo) RecordingMetadata.Text += $" · {info.Width}×{info.Height} · {_editorFrameRate:0.##} FPS";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (ReferenceEquals(_selected, recording) && !_closing) SetStatus(error.Message, true); }
    }
    private async Task LoadWaveformsAsync(RecordingManifest recording, CancellationToken token)
    {
        try
        {
            var waves = await PreviewCache.GetWaveformsAsync(recording, token);
            if (!token.IsCancellationRequested && ReferenceEquals(_selected, recording) && !_closing)
            { Timeline.Waveforms = waves; PreviewDetail.Text = "빠른 미리보기 · 내보내기는 원본 기준"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (ReferenceEquals(_selected, recording) && !_closing)
            { PreviewDetail.Text = "파형 분석 실패 · 영상 탐색과 재생은 가능"; SetStatus(error.Message, true); }
        }
    }

    private async Task RequestSliceAsync(double position, bool play, bool debounce = false)
    {
        var recording = _selected;
        if (recording == null || Smoke || _closing) return;
        var target = Math.Clamp(position, 0, Math.Max(0, recording.DurationSeconds - 0.001));
        _pendingPosition = target; _playOnOpen = play;
        var tracks = CurrentMix(); var key = MixKey(recording, tracks);
        if (_previewSlice?.Contains(target) == true && _previewKey == key && _mediaReady && !_editorLoading)
        {
            Player.Position = TimeSpan.FromSeconds(target - _previewSlice.Start);
            if (play) StartPlayback(); else PausePlayback();
            return;
        }
        var window = Math.Floor(target / VideoView.Core.Media.PreviewCache.WindowSeconds);
        if (_editorLoading && _loadingWindow == window && _loadingMix == key) return;
        _sliceCancellation?.Cancel();
        var generation = ++_sliceGeneration;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_editorCancellation?.Token ?? CancellationToken.None);
        _sliceCancellation = cancellation;
        var token = cancellation.Token;
        PreviewSlice? prepared = null;
        if (_prefetchedSlice?.Contains(target) == true && _previewKey == key)
        { prepared = _prefetchedSlice; _prefetchedSlice = null; }
        CancelPrefetch();
        _loadingWindow = window; _loadingMix = key; _editorLoading = true;
        PausePlayback(); _mediaReady = false;
        PlayButton.IsEnabled = ApplyMixButton.IsEnabled = false;
        PreviewMessage.Text = $"{ClipTimeline.Time(target)} 구간 준비 중…";
        EditorPlaceholder.Visibility = Visibility.Visible;
        try
        {
            if (debounce && prepared == null) await Task.Delay(140, token);
            prepared ??= await PreviewCache.GetSliceAsync(recording, tracks, target, token);
            token.ThrowIfCancellationRequested();
            if (generation != _sliceGeneration || !ReferenceEquals(_selected, recording) || _closing) return;
            Player.Close(); _previewSlice?.Dispose();
            _previewSlice = prepared; prepared = null;
            _previewMedia = _previewSlice.Path; _previewKey = key;
            // WPF clears its pending Source change on Play, not Pause. Opening with Pause
            // can reopen the file on the subsequent Position assignment and lose the seek.
            // Keep this initial open silent; MediaOpened pauses and seeks before unmuting.
            Player.IsMuted = true;
            Player.Source = new Uri(_previewMedia);
            Player.Play();
            UpdateMixHint();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation == _sliceGeneration && !_closing)
            { PreviewMessage.Text = "미리보기 준비 실패 · 재생을 눌러 다시 시도하세요"; SetStatus(error.Message, true); }
        }
        finally
        {
            prepared?.Dispose(); cancellation.Dispose();
            if (generation == _sliceGeneration)
            {
                _sliceCancellation = null; _editorLoading = false;
                PlayButton.IsEnabled = ApplyMixButton.IsEnabled = !_busy;
            }
        }
    }
    private void CancelPrefetch()
    {
        _failedPrefetchStart = -1;
        _prefetchCancellation?.Cancel(); _prefetchCancellation = null;
        _prefetchedSlice?.Dispose(); _prefetchedSlice = null;
    }
    private async Task PrefetchAsync(RecordingManifest recording, ExportTrack[] tracks, double start, CancellationTokenSource cancellation)
    {
        PreviewSlice? slice = null;
        var token = cancellation.Token; var key = MixKey(recording, tracks);
        try
        {
            slice = await PreviewCache.GetSliceAsync(recording, tracks, start, token);
            if (!token.IsCancellationRequested && !_closing && ReferenceEquals(_selected, recording) && key == _previewKey)
            { _prefetchedSlice?.Dispose(); _prefetchedSlice = slice; slice = null; }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Do not retry failed background work on every timer tick. A foreground seek can retry.
            if (ReferenceEquals(_prefetchCancellation, cancellation)) _failedPrefetchStart = start;
        }
        finally
        {
            slice?.Dispose(); cancellation.Dispose();
            if (ReferenceEquals(_prefetchCancellation, cancellation)) _prefetchCancellation = null;
        }
    }
    private void ClearPreviewMedia()
    {
        ++_sliceGeneration; _sliceCancellation?.Cancel(); _sliceCancellation = null;
        CancelPrefetch(); _previewSlice?.Dispose(); _previewSlice = null;
        _previewMedia = _previewKey = null; _mediaReady = false; _editorLoading = false;
    }
    private void Mix_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AudioRow.Gain) or nameof(AudioRow.Enabled))) return;
        _playOnOpen = false; PausePlayback(); _sliceCancellation?.Cancel(); _loadingMix = null; CancelPrefetch();
        UpdateMixHint(); QueueEditSave();
    }
    private void UpdateMixHint()
    {
        var dirty = _selected != null && _previewKey != MixKey(_selected, CurrentMix());
        ApplyMixButton.Content = dirty ? "변경한 소리 적용 •" : "소리 적용하고 듣기";
        PlaybackHint.Text = dirty ? "소리 변경됨 · 재생 또는 소리 적용을 누르면 반영됩니다." :
            "음원별 원본은 유지됩니다. Space 재생 · I/O 자르기 · M 중요 지점";
    }
    private async void RenderPreview_Click(object sender, RoutedEventArgs e) => await TrackEditorWork(RenderPreviewAsync());
    private async Task RenderPreviewAsync()
    {
        if (_selected == null || _busy) return;
        CommitTrimText();
        await RequestSliceAsync(Timeline.Position >= _playStart && Timeline.Position < _playEnd ? Timeline.Position : _playStart, true);
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) { PausePlayback(); return; }
        await TrackEditorWork(RenderPreviewAsync());
    }
    private void StartPlayback() { _isPlaying = true; Player.Play(); PlayButton.Content = "Ⅱ  일시정지"; }
    private void PausePlayback() { _isPlaying = false; Player.Pause(); PlayButton.Content = "▶  재생"; }
    private void Player_Opened(object sender, RoutedEventArgs e)
    {
        if (_previewSlice == null || _selected == null || _closing) return;
        _mediaReady = true;
        Player.Pause();
        Player.Position = TimeSpan.FromSeconds(Math.Clamp(_pendingPosition - _previewSlice.Start, 0, _previewSlice.End - _previewSlice.Start));
        Player.IsMuted = false;
        if (_playOnOpen) StartPlayback(); else PausePlayback();
        EditorPlaceholder.Visibility = _selected.HasVideo ? Visibility.Collapsed : Visibility.Visible;
        PreviewMessage.Text = "음성 녹음 · 파형에서 구간을 선택하세요";
    }
    private void Player_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false; PausePlayback(); PreviewMessage.Text = "재생 실패 · 재생을 눌러 다시 시도하세요";
        EditorPlaceholder.Visibility = Visibility.Visible; SetStatus(e.ErrorException.Message, true);
    }
    private void TickEditor()
    {
        if (Player.Source != null && _previewSlice != null && _isPlaying && !Timeline.IsDragging)
        {
            var position = _previewSlice.Start + Player.Position.TotalSeconds;
            if (position >= _playEnd - 0.025) EndSelectionPlayback();
            else
            {
                Timeline.Position = Math.Clamp(position, 0, _selected!.DurationSeconds);
                if (_previewSlice.End < _selected.DurationSeconds - 0.025 && _previewSlice.End < _playEnd &&
                    _previewSlice.End - position <= 5 && _prefetchedSlice == null && _prefetchCancellation == null &&
                    _failedPrefetchStart != _previewSlice.End)
                {
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_editorCancellation!.Token);
                    _prefetchCancellation = cancellation;
                    _ = TrackEditorWork(PrefetchAsync(_selected, CurrentMix(), _previewSlice.End, cancellation));
                }
            }
        }
        PlaybackClock.Text = $"{ClipTimeline.Time(Timeline.Position)} / {ClipTimeline.Time(_selected?.DurationSeconds ?? 0)}";
    }
    private void EndSelectionPlayback()
    {
        PausePlayback(); Timeline.Position = _playStart;
        _ = TrackEditorWork(RequestSliceAsync(_playStart, LoopSelection.IsChecked == true));
    }
    private void Player_Ended(object sender, RoutedEventArgs e)
    {
        if (!_isPlaying || _previewSlice == null || _selected == null) return;
        if (_previewSlice.End < Math.Min(_playEnd, _selected.DurationSeconds) - 0.025)
        { Timeline.Position = _previewSlice.End; _ = TrackEditorWork(RequestSliceAsync(_previewSlice.End, true)); }
        else EndSelectionPlayback();
    }
    private void SeekTo(double seconds)
    {
        if (_selected == null || _busy) return;
        var position = Math.Clamp(seconds, 0, _selected.DurationSeconds);
        Timeline.Position = position; QueueEditSave();
        _ = TrackEditorWork(RequestSliceAsync(position, _isPlaying || (_editorLoading && _playOnOpen), debounce: true));
        PlaybackClock.Text = $"{ClipTimeline.Time(position)} / {ClipTimeline.Time(_selected.DurationSeconds)}";
    }
}
