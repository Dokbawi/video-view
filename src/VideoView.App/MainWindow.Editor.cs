using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VideoView.App.Controls;
using VideoView.Core;
using VideoView.Core.Media;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace VideoView.App;

public partial class MainWindow
{
    private bool _isPlaying, _playOnOpen, _editorLoading, _refreshingLibrary;
    private double _pendingPosition;
    private double _editorFrameRate = 30;
    private CancellationTokenSource? _editorCancellation;
    private readonly HashSet<Task> _editorTasks = [];
    private readonly List<(double Start, double End)> _trimHistory = [];

    private void InitializeEditor()
    {
        Timeline.EditStarted += PushTrimHistory;
        Timeline.RangeChanged += SyncTrimRange;
        Timeline.SeekRequested += SeekTo;
        PreviewKeyDown += Editor_KeyDown;
        SizeChanged += (_, _) => Timeline.Height = ActualHeight < 800 ? 112 : 140;
    }

    private void ArrangeSettings()
    {
        var cards = SettingsPage.Children.Cast<UIElement>().ToArray(); SettingsPage.Children.Clear();
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new());
        var left = new StackPanel(); var right = new StackPanel(); Grid.SetColumn(right, 2);
        left.Children.Add(cards[0]); left.Children.Add(cards[1]);
        ((FrameworkElement)cards[2]).Margin = new Thickness(0); right.Children.Add(cards[2]);
        var engine = (Border)cards[3]; var engineContent = (StackPanel)engine.Child;
        engineContent.Children.RemoveAt(0); engine.Child = null;
        engine.Child = new Expander { Header = "고급 · 녹화 엔진 경로", Foreground = (Brush)FindResource("Muted"), Content = engineContent };
        right.Children.Add(engine); grid.Children.Add(left); grid.Children.Add(right);
        SettingsPage.Children.Add(grid);
        ((FrameworkElement)cards[4]).Margin = new Thickness(0);
        SettingsSaveHost.Child = cards[4];
    }

    private async void Recording_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingLibrary) return;
        if (!FlushEdits())
        {
            _refreshingLibrary = true;
            try { RecordingsList.SelectedItem = _libraryItems.FirstOrDefault(r => r.Id == _selected?.Id); }
            finally { _refreshingLibrary = false; }
            return;
        }
        _editorCancellation?.Cancel();
        _editorCancellation?.Dispose(); _editorCancellation = null;
        PausePlayback(); Player.Close(); ClearPreviewMedia(); _trimHistory.Clear();
        foreach (var row in _exportRows) row.PropertyChanged -= Mix_Changed;
        _selected = RecordingsList.SelectedItem as RecordingManifest;
        EditorPanel.IsEnabled = _selected != null;
        EditorPanel.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        LibraryWelcome.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
        _exportRows.Clear(); Timeline.Reset(_selected?.DurationSeconds ?? 1);
        EditorPlaceholder.Visibility = Visibility.Visible;
        if (_selected == null) { SetLibraryListVisible(true); _editorLoading = false; return; }
        var recording = _selected;
        RecordingTitle.Text = recording.Title;
        var size = !Smoke && File.Exists(recording.MediaPath) ? $" · {new FileInfo(recording.MediaPath).Length / 1_000_000d:F1} MB" : "";
        RecordingMetadata.Text = $"{recording.CreatedAt:yyyy.MM.dd  HH:mm} · {(recording.HasVideo ? "화면 + 음성" : "음성 녹음")} · {recording.Tracks.Count}개 음원{size}";
        RecordingMetadata.ToolTip = recording.MediaPath;
        foreach (var track in recording.Tracks)
        {
            var row = new AudioRow(new(track.Index.ToString(), track.Name, VideoView.Platform.Audio.AudioSourceKind.Application))
                { Enabled = true, Index = track.Index };
            row.PropertyChanged += Mix_Changed; _exportRows.Add(row);
        }
        RestoreEdits(recording); SetLibraryListVisible(false);
        PreviewDetail.Text = "현재 구간 먼저 준비 · 파형은 뒤에서 분석";
        if (Smoke)
        {
            Timeline.Waveforms = recording.Tracks.Select((track, index) => new AudioWaveform(track.Name,
                Enumerable.Range(0, 1600).Select(i => (float)(0.12 * Math.Abs(Math.Sin(i * (0.06 + index * 0.02))) *
                    (0.2 + 0.8 * Math.Abs(Math.Sin(i * 0.012))))).ToArray())).ToArray();
            Timeline.SetRange(8.4, 48.6); Timeline.Position = 24.2; SyncTrimRange();
            _bookmarks.AddRange([17.3, 35.8, 46.2]); RefreshBookmarks();
            PreviewMessage.Text = "영상 미리보기"; return;
        }
        var cancellation = new CancellationTokenSource(); _editorCancellation = cancellation;
        var task = LoadEditorAsync(recording, cancellation); _editorTasks.Add(task);
        try { await task; } finally { _editorTasks.Remove(task); }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => SeekTo(Timeline.Position - 5);
    private void Forward_Click(object sender, RoutedEventArgs e) => SeekTo(Timeline.Position + 5);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Timeline.Zoom(0.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Timeline.Zoom(2);
    private void FitTimeline_Click(object sender, RoutedEventArgs e) => Timeline.Fit();

    private void PushTrimHistory()
    {
        if (_busy || _selected == null) return;
        var range = (Timeline.SelectionStart, Timeline.SelectionEnd);
        if (_trimHistory.Count > 0 && _trimHistory[^1] == range) return;
        if (_trimHistory.Count >= 100) _trimHistory.RemoveAt(0);
        _trimHistory.Add(range);
    }
    private void SyncTrimRange()
    {
        _playStart = Timeline.SelectionStart; _playEnd = Timeline.SelectionEnd;
        TrimStart.Text = _playStart.ToString("0.###", CultureInfo.InvariantCulture);
        TrimEnd.Text = _playEnd.ToString("0.###", CultureInfo.InvariantCulture);
        SelectionSummary.Text = $"선택 {ClipTimeline.Time(_playEnd - _playStart)}";
        ExportButton.ToolTip = $"선택한 {ClipTimeline.Time(_playEnd - _playStart)} 구간으로 공유 파일 만들기";
        QueueEditSave();
        if (_isPlaying && (Timeline.Position < _playStart || Timeline.Position >= _playEnd)) { PausePlayback(); SeekTo(_playStart); }
    }
    private void CommitTrimText()
    {
        if (_selected == null) return;
        var start = ReadNumber(TrimStart.Text, "시작 시간"); var end = ReadNumber(TrimEnd.Text, "끝 시간");
        if (start < 0 || end > _selected.DurationSeconds + 0.001 || end - start < Math.Min(0.05, _selected.DurationSeconds))
            throw new ArgumentException("시작·끝은 원본 길이 안에서 지정하세요. 선택 구간은 최소 0.05초입니다.");
        if (Math.Abs(start - Timeline.SelectionStart) > 0.0005 || Math.Abs(end - Timeline.SelectionEnd) > 0.0005) PushTrimHistory();
        Timeline.SetRange(start, end); SyncTrimRange();
    }
    private void Trim_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    { if (_selected == null || _busy) return; try { CommitTrimText(); } catch (Exception error) { SetStatus(error.Message, true); SyncTrimRange(); } }
    private void MarkIn_Click(object sender, RoutedEventArgs e)
    { if (_busy) return; PushTrimHistory(); Timeline.SetRange(Math.Min(Timeline.Position, Timeline.SelectionEnd - 0.05), Timeline.SelectionEnd); SyncTrimRange(); }
    private void MarkOut_Click(object sender, RoutedEventArgs e)
    { if (_busy) return; PushTrimHistory(); Timeline.SetRange(Timeline.SelectionStart, Math.Max(Timeline.Position, Timeline.SelectionStart + 0.05)); SyncTrimRange(); }
    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    { if (_busy) return; PushTrimHistory(); Timeline.SetRange(0, Timeline.Duration); Timeline.Fit(); SyncTrimRange(); }
    private void UndoTrim_Click(object sender, RoutedEventArgs e)
    { if (_busy || _trimHistory.Count == 0) return; var range = _trimHistory[^1]; _trimHistory.RemoveAt(_trimHistory.Count - 1); Timeline.SetRange(range.Start, range.End); SyncTrimRange(); }
    private void SetLibraryListVisible(bool visible)
    { LibraryListColumn.Width = new GridLength(visible ? 230 : 0); LibraryListPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
    private void ToggleLibrary_Click(object sender, RoutedEventArgs e) => SetLibraryListVisible(LibraryListPanel.Visibility != Visibility.Visible);

    private void Editor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (LibraryPage.Visibility != Visibility.Visible || _selected == null || _busy || _editorLoading ||
            e.OriginalSource is TextBoxBase or ComboBox or Slider or ToggleButton) return;
        if (e.Key == Key.Space) Play_Click(this, new());
        else if (e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.None) MarkIn_Click(this, new());
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.None) MarkOut_Click(this, new());
        else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None) Bookmark_Click(this, new());
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) UndoTrim_Click(this, new());
        else if (e.Key is Key.Left or Key.Right)
            SeekTo(Timeline.Position + (e.Key == Key.Left ? -1 : 1) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1d / _editorFrameRate : 5));
        else if (e.Key == Key.Home) SeekTo(Timeline.SelectionStart);
        else if (e.Key == Key.End) SeekTo(Timeline.SelectionEnd);
        else return;
        e.Handled = true;
    }

    private void Shortcut_Focus(object sender, KeyboardFocusChangedEventArgs e) { _hotkey?.Suspend(); ShortcutFeedback.Text = "원하는 키 조합을 누르세요. Esc는 입력 종료."; }
    private void Shortcut_Blur(object sender, KeyboardFocusChangedEventArgs e)
    { if (Smoke || _closing) return; try { _hotkey?.Register(_settings); } catch (Exception error) { ShortcutFeedback.Text = error.Message; } }
    private void Shortcut_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape) { Keyboard.ClearFocus(); return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) { ShortcutFeedback.Text = "Ctrl·Alt·Shift·Win 중 보조키를 하나 이상 함께 누르세요."; return; }
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl"); if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift"); if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(((System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(key)).ToString());
        ((TextBox)sender).Text = string.Join('+', parts);
        try
        {
            var shortcuts = new[] { HotkeyText.Text, RecordHotkeyText.Text, BufferHotkeyText.Text, BookmarkHotkeyText.Text }.Select(GlobalHotkey.Parse).ToArray();
            ShortcutFeedback.Text = shortcuts.Distinct().Count() == 4 ? "설정 저장을 누르면 적용됩니다." : "중복된 단축키가 있습니다. 동작마다 다른 키를 지정하세요.";
        }
        catch (ArgumentException error) { ShortcutFeedback.Text = error.Message; }
    }
}
