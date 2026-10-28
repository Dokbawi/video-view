using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoView.Core;
using VideoView.Core.Library;
using VideoView.Core.Media;
using VideoView.Platform.Audio;
using VideoView.Platform.Recording;
using VideoView.Platform.Video;

namespace VideoView.App;

public partial class MainWindow : Window
{
    private readonly string[] _arguments;
    private readonly SettingsStore _store;
    private AppSettings _settings;
    private readonly ObservableCollection<AudioRow> _applicationRows = [];
    private readonly ObservableCollection<AudioRow> _exportRows = [];
    private readonly AudioRow _systemRow = new(new("system", "컴퓨터 전체 소리", AudioSourceKind.System)) { Enabled = true };
    private readonly DispatcherTimer _timer;
    private readonly List<IAudioSource> _testSources = [];
    private RecordingSession? _session;
    private ScreenCaptureSource? _preview;
    private GlobalHotkey? _hotkey;
    private System.Windows.Forms.NotifyIcon? _tray;
    private RecordingManifest? _selected;
    private ShareWindow? _shareWindow;
    private string? _previewMedia;
    private string? _previewKey;
    private double _playStart, _playEnd;
    private WriteableBitmap? _previewBitmap;
    private long _previewSequence = -1;
    private bool _ready, _busy, _captureBusy, _replaySaving, _exiting, _closing, _cleanupComplete, _firstRecordingConfirmed;
    private bool _libraryRefreshPending;
    private TaskCompletionSource? _workCompletion, _captureCompletion, _replayCompletion;
    private bool _autoTargetsResolved = true;
    private bool Smoke => _arguments.Contains("--ui-smoke");

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        // Smoke rendering never reads or writes the real user's configuration.
        var dataIndex = Array.IndexOf(arguments, "--data-directory");
        var dataDirectory = dataIndex >= 0 && dataIndex + 1 < arguments.Length ? Path.GetFullPath(arguments[dataIndex + 1]) : null;
        _store = new SettingsStore(Smoke ? Path.Combine(AppContext.BaseDirectory, "smoke-settings.json") : dataDirectory == null ? null : Path.Combine(dataDirectory, "settings.json"));
        _settings = Smoke ? new AppSettings() : _store.Load();
        if (dataDirectory != null && !File.Exists(_store.FilePath))
        {
            _settings.OutputDirectory = Path.Combine(dataDirectory, "Recordings");
            _settings.BufferDirectory = Path.Combine(dataDirectory, "Buffer");
        }
        InitializeComponent();
        if (Smoke && arguments.Contains("--compact")) { Width = MinWidth; Height = MinHeight; }
        InitializeEditor();
        // Keep recording actions together beside the live source; retain space for the mixer below.
        var recordingCard = (Border)HomePage.Children[HomePage.Children.Count - 1];
        HomePage.Children.Remove(recordingCard);
        recordingCard.Style = null; recordingCard.Margin = new Thickness(0, 0, 0, 16);
        recordingCard.Padding = new Thickness(0, 0, 0, 16); recordingCard.BorderThickness = new Thickness(0, 0, 0, 1);
        recordingCard.BorderBrush = (Brush)FindResource("Line");
        ((StackPanel)((Border)((Grid)HomePage.Children[0]).Children[1]).Child).Children.Insert(0, recordingCard);
        ArrangeSettings();
        ApplicationsList.ItemsSource = _applicationRows;
        ExportTracks.ItemsSource = _exportRows;
        PopulateSettings();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _tray != null)
            { _playOnOpen = false; PausePlayback(); Hide(); }
        };
        _ready = true;
        ShowPage("home");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Smoke)
        {
            TargetBox.ItemsSource = new[] { new { Name = "디스플레이 1 · 2560 × 1440" } };
            TargetBox.SelectedIndex = 0;
            MicBox.ItemsSource = new[] { new { Name = "기본 마이크" } };
            MicBox.SelectedIndex = 0;
            _applicationRows.Add(new(new("preview", "Discord", AudioSourceKind.Application, 1)));
            var pageIndex = Array.IndexOf(_arguments, "--page");
            var page = pageIndex >= 0 && pageIndex + 1 < _arguments.Length ? _arguments[pageIndex + 1] : "home";
            if (page == "library")
            {
                var sample = new RecordingManifest { Title = "테스트 녹화 · 음원별 편집", HasVideo = true, DurationSeconds = 60,
                    Tracks = [new(0, "내 마이크"), new(1, "Discord"), new(2, "게임 소리")] };
                _libraryItems = [sample]; ApplyLibraryFilter(); RecordingsList.SelectedItem = sample;
                LibraryEmpty.Visibility = Visibility.Collapsed;
                if (_arguments.Contains("--library-list")) SetLibraryListVisible(true);
                if (_arguments.Contains("--saved-receipt")) ShowSavedFile(Path.Combine(AppContext.BaseDirectory, "sample.mp4"), null,
                    "공유 파일 완성 · 7.42 MB · VideoView_sample.mp4");
            }
            ShowPage(page);
            var pathIndex = Array.IndexOf(_arguments, "--ui-smoke") + 1;
            var path = pathIndex < _arguments.Length ? _arguments[pathIndex] : Path.Combine(AppContext.BaseDirectory, "ui.png");
            var shareIndex = Array.IndexOf(_arguments, "--share-stage");
            if (shareIndex >= 0 && shareIndex + 1 < _arguments.Length && _selected != null)
            {
                var dialog = new ShareWindow(_selected, _playStart, _playEnd, CurrentMix(), Tools(),
                    _settings.OutputDirectory, _settings.TargetMegabytes, true) { Owner = this };
                dialog.ConfigureSmoke(_arguments[shareIndex + 1], Path.GetFullPath(path));
                dialog.ShowDialog(); _exiting = true; Close(); return;
            }
            await Task.Delay(350);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            UpdateLayout();
            var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) png.Save(output);
            if (page == "library")
            {
                var bounds = ExportButton.TransformToAncestor(content).TransformBounds(new Rect(0, 0, ExportButton.ActualWidth, ExportButton.ActualHeight));
                var mixerBounds = MixerExportPanel.TransformToAncestor(content).TransformBounds(new Rect(0, 0, MixerExportPanel.ActualWidth, MixerExportPanel.ActualHeight));
                var actionBounds = RecordingActions.TransformToAncestor(content).TransformBounds(new Rect(0, 0, RecordingActions.ActualWidth, RecordingActions.ActualHeight));
                File.WriteAllText(path + ".layout.json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    Width = content.ActualWidth, Height = content.ActualHeight,
                    ExportVisible = bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= content.ActualWidth && bounds.Bottom <= content.ActualHeight,
                    MixerVisible = mixerBounds.Top >= 0 && mixerBounds.Bottom <= content.ActualHeight - 35,
                    ActionsVisible = actionBounds.Left >= 0 && actionBounds.Right <= content.ActualWidth,
                    PreviewHeight = EditorPreview.ActualHeight, TimelineWidth = Timeline.ActualWidth
                }));
            }
            _exiting = true; Close(); return;
        }
        await GuardAsync(async () =>
        {
            await RefreshSourcesAsync();
            _hotkey = new GlobalHotkey(this);
            string? hotkeyError = null;
            try { _hotkey.Register(_settings); }
            catch (Exception exception) { hotkeyError = exception.Message; }
            _hotkey.Pressed += action =>
            {
                if (action == HotkeyAction.Record) Record_Click(this, new());
                else if (action == HotkeyAction.Buffer) ReplayToggle_Click(this, new());
                else if (action == HotkeyAction.Bookmark) MarkMoment_Click(this, new());
                else _ = GuardReplayAsync();
            };
            SetupTray();
            if (_store.LoadWarning != null) SetStatus(_store.LoadWarning, true);
            else if (!File.Exists(_store.FilePath)) SetStatus($"첫 저장 위치: {_settings.OutputDirectory} · 설정에서 변경할 수 있습니다.");
            if (hotkeyError != null) { SetStatus(hotkeyError, true); ShortcutFeedback.Text = hotkeyError; Notify("단축키 설정 확인", hotkeyError, true); }
            var recovery = Path.Combine(_settings.OutputDirectory, ".sessions");
            if (Directory.Exists(recovery) && Directory.EnumerateFiles(recovery, "session.json", SearchOption.AllDirectories).Any())
                SetStatus("중단된 녹화 조각 발견. 설정 → ‘중단된 연속 녹화 복구’에서 복구할 수 있습니다.");
            if (_settings.AutoStartReplay)
            {
                _firstRecordingConfirmed = true;
                if (!_autoTargetsResolved) SetStatus("이전 녹화 대상을 찾지 못해 자동 기록하지 않았습니다. 화면·음원을 다시 선택해 주세요.", true);
                else await StartSessionAsync(false, true);
            }
            if (_arguments.Contains("--startup")) { WindowState = WindowState.Minimized; Hide(); }
        });
    }

    private void PopulateSettings()
    {
        ModeBox.SelectedIndex = _settings.Mode == CaptureMode.AudioOnly ? 0 : 1;
        ReplayBox.SelectedIndex = Array.IndexOf(new[] { 30, 60, 180, 300 }, _settings.ReplaySeconds);
        ResolutionBox.SelectedIndex = _settings.Height == 720 ? 0 : 1;
        FpsBox.SelectedIndex = _settings.FramesPerSecond == 60 ? 1 : 0;
        QualityBox.SelectedIndex = (int)_settings.Quality;
        OutputPath.Text = _settings.OutputDirectory;
        BufferPath.Text = _settings.BufferDirectory;
        BufferLimit.Text = ((double)_settings.BufferLimitBytes / (1024 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture);
        HotkeyText.Text = _settings.ReplayHotkey;
        RecordHotkeyText.Text = _settings.RecordHotkey;
        BufferHotkeyText.Text = _settings.BufferHotkey;
        BookmarkHotkeyText.Text = _settings.BookmarkHotkey;
        HotkeyHint.Text = _settings.ReplayHotkey.Replace("+", " + ");
        StartupCheck.IsChecked = _settings.StartWithWindows;
        AutoReplayCheck.IsChecked = _settings.AutoStartReplay;
        HideBorderCheck.IsChecked = _settings.HideCaptureBorder;
        CaptureCursorCheck.IsChecked = _settings.CaptureCursor;
        FfmpegPathBox.Text = _settings.FfmpegPath;
        FfprobePathBox.Text = _settings.FfprobePath;
        AudioScopeBox.SelectedIndex = _settings.AudioInputs.Any(a => a.Enabled && a.Kind == AudioKind.System) ? 1 : 0;
        _systemRow.Gain = _settings.AudioInputs.FirstOrDefault(a => a.Kind == AudioKind.System)?.Gain ?? 1;
        UpdateCaptureInfo();
    }

    private async Task RefreshSourcesAsync()
    {
        if (_session?.IsRunning == true) throw new InvalidOperationException("대상 목록 갱신은 기록을 멈춘 뒤 가능합니다.");
        StopPreview(); StopAudioTest();
        var targetId = (TargetBox.SelectedItem as CaptureTarget)?.Id ?? _settings.CaptureTargetId;
        var targetName = (TargetBox.SelectedItem as CaptureTarget)?.Name ?? _settings.CaptureTargetName;
        var targets = CaptureTargetCatalog.List();
        TargetBox.ItemsSource = targets;
        TargetBox.SelectedItem = targets.FirstOrDefault(t => t.Id == targetId && (targetName == null || t.Name == targetName));
        if (_settings.Mode == CaptureMode.ScreenAndAudio && TargetBox.SelectedItem == null) _autoTargetsResolved = false;
        var microphones = await Task.Run(AudioDeviceCatalog.ListMicrophones);
        var mic = _settings.AudioInputs.FirstOrDefault(a => a.Kind == AudioKind.Microphone);
        MicBox.ItemsSource = microphones;
        MicBox.SelectedItem = microphones.FirstOrDefault(m => m.Id == mic?.Id) ?? microphones.FirstOrDefault();
        MicEnabled.IsChecked = mic?.Enabled ?? false;
        MicGain.Value = mic?.Gain ?? 1;
        if (mic?.Enabled == true && microphones.All(m => m.Id != mic.Id)) { MicEnabled.IsChecked = false; _autoTargetsResolved = false; }
        var previous = _applicationRows.ToDictionary(a => (a.Source.Id, a.Source.Name), a => (a.Enabled, a.Gain));
        var apps = await Task.Run(AudioDeviceCatalog.ListApplications);
        _applicationRows.Clear();
        foreach (var descriptor in apps)
        {
            var saved = _settings.AudioInputs.FirstOrDefault(a => a.Kind == AudioKind.Application && a.Id == descriptor.Id && a.Name == descriptor.Name);
            var row = new AudioRow(descriptor) { Enabled = saved?.Enabled ?? false, Gain = saved?.Gain ?? 1 };
            if (previous.TryGetValue((descriptor.Id, descriptor.Name), out var old)) { row.Enabled = old.Enabled; row.Gain = old.Gain; }
            _applicationRows.Add(row);
        }
        if (_settings.AudioInputs.Where(a => a.Enabled && a.Kind == AudioKind.Application).Any(a => !apps.Any(d => d.Id == a.Id && d.Name == a.Name))) _autoTargetsResolved = false;
        AudioScope_Changed(this, null!);
    }

    private AppSettings ReadSettings(bool useEditedHotkeys = false)
    {
        var inputs = new List<AudioInput>();
        if (MicEnabled.IsChecked == true && MicBox.SelectedItem is AudioSourceDescriptor microphone)
            inputs.Add(new(microphone.Id, microphone.Name, AudioKind.Microphone) { Gain = (float)MicGain.Value });
        if (AudioScopeBox.SelectedIndex == 1 && _systemRow.Enabled) inputs.Add(new("system", "컴퓨터 전체 소리", AudioKind.System) { Gain = (float)_systemRow.Gain });
        if (AudioScopeBox.SelectedIndex == 0)
            inputs.AddRange(_applicationRows.Where(r => r.Enabled).Select(r => new AudioInput(r.Source.Id, r.Name, AudioKind.Application, r.Source.ProcessId) { Gain = (float)r.Gain }));
        var target = TargetBox.SelectedItem as CaptureTarget;
        var settings = new AppSettings
        {
            OutputDirectory = Environment.ExpandEnvironmentVariables(OutputPath.Text.Trim()),
            BufferDirectory = Environment.ExpandEnvironmentVariables(BufferPath.Text.Trim()),
            FfmpegPath = FfmpegPathBox.Text.Trim(), FfprobePath = FfprobePathBox.Text.Trim(),
            Mode = ModeBox.SelectedIndex == 0 ? CaptureMode.AudioOnly : CaptureMode.ScreenAndAudio,
            Height = ResolutionBox.SelectedIndex == 0 ? 720 : 1080, FramesPerSecond = FpsBox.SelectedIndex == 0 ? 30 : 60,
            Quality = (Quality)QualityBox.SelectedIndex, ReplaySeconds = new[] { 30, 60, 180, 300 }[Math.Max(0, ReplayBox.SelectedIndex)],
            BufferLimitBytes = checked((long)(ReadNumber(BufferLimit.Text, "임시 저장 한도") * 1024 * 1024 * 1024)),
            TargetMegabytes = _settings.TargetMegabytes,
            ReplayHotkey = useEditedHotkeys ? HotkeyText.Text.Trim() : _settings.ReplayHotkey,
            RecordHotkey = useEditedHotkeys ? RecordHotkeyText.Text.Trim() : _settings.RecordHotkey,
            BufferHotkey = useEditedHotkeys ? BufferHotkeyText.Text.Trim() : _settings.BufferHotkey,
            BookmarkHotkey = useEditedHotkeys ? BookmarkHotkeyText.Text.Trim() : _settings.BookmarkHotkey,
            StartWithWindows = StartupCheck.IsChecked == true,
            HideCaptureBorder = HideBorderCheck.IsChecked == true,
            CaptureCursor = CaptureCursorCheck.IsChecked == true,
            AutoStartReplay = AutoReplayCheck.IsChecked == true, AudioInputs = inputs,
            CaptureTargetId = target?.Id, CaptureTargetName = target?.Name
        };
        settings.Validate();
        return settings;
    }

    private FfmpegTools Tools() => FfmpegTools.Resolve(_settings.FfmpegPath, _settings.FfprobePath);

    private async Task StartSessionAsync(bool continuous, bool replay)
    {
        var settings = ReadSettings();
        var target = TargetBox.SelectedItem as CaptureTarget;
        if (settings.Mode == CaptureMode.ScreenAndAudio && settings.AudioInputs.Count == 0 && AudioScopeBox.SelectedIndex != 2)
            throw new ArgumentException("녹음할 소리를 선택해 주세요. 화면만 저장하려면 ‘컴퓨터 소리 제외’를 직접 선택하면 됩니다.");
        if (!_firstRecordingConfirmed && !File.Exists(_store.FilePath))
        {
            if (MessageBox.Show(this, $"녹화 파일을 아래 위치에 저장합니다.\n\n{settings.OutputDirectory}\n\n이 위치로 시작할까요? 다른 위치는 ‘설정’에서 선택할 수 있습니다.", "첫 저장 위치 확인", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            _firstRecordingConfirmed = true;
        }
        SetCaptureControls(false);
        try
        {
            StopPreview(); StopAudioTest();
            if (_session != null) { await _session.DisposeAsync(); _session = null; }
            _settings = settings;
            _store.Save(_settings);
            _session = new RecordingSession(_settings, target, Tools());
            _session.Status += text => Dispatcher.BeginInvoke(() => SetStatus(text));
            _session.Faulted += text => Dispatcher.BeginInvoke(() => { SetStatus(text, true); Notify("기록 중단", text, true); });
            _session.Saved += recording => Dispatcher.BeginInvoke(() =>
            {
                RefreshLibrary();
                var detail = $"저장 완료 · {recording.Title} · {VideoView.App.Controls.ClipTimeline.Time(recording.DurationSeconds)}";
                ShowSavedFile(recording.MediaPath, recording.Id, detail); Notify("저장 완료", detail);
            });
            await _session.StartAsync(continuous, replay);
        }
        catch { SetCaptureControls(true); throw; }
    }

    private async Task StopSessionAsync()
    {
        if (_session == null) return;
        var session = _session; _session = null;
        await session.DisposeAsync();
        SetCaptureControls(true);
        CapturePreview.Source = null; _previewBitmap = null; _previewSequence = -1;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        RefreshLibrary();
    }

    private async void Record_Click(object sender, RoutedEventArgs e) => await GuardCaptureAsync(async () =>
    {
        if (_session?.IsRunning != true) await StartSessionAsync(true, false);
        else if (_session.IsRecording)
        {
            if (_session.IsBuffering) await _session.SetContinuousAsync(false);
            else await StopSessionAsync();
        }
        else await _session.SetContinuousAsync(true);
    });

    private async void ReplayToggle_Click(object sender, RoutedEventArgs e) => await GuardCaptureAsync(async () =>
    {
        if (_session?.IsRunning != true) await StartSessionAsync(false, true);
        else if (_session.IsBuffering)
        {
            if (_session.IsRecording) await _session.SetReplayAsync(false);
            else await StopSessionAsync();
        }
        else await _session.SetReplayAsync(true);
    });

    private async void SaveReplay_Click(object sender, RoutedEventArgs e) => await GuardReplayAsync();
    private bool _bookmarkBusy;
    private async void MarkMoment_Click(object sender, RoutedEventArgs e)
    {
        if (_bookmarkBusy || _closing) return;
        _bookmarkBusy = true;
        try
        {
            var session = _session ?? throw new InvalidOperationException("먼저 연속 녹화 또는 최근 구간 보관을 켜 주세요.");
            var position = await session.AddBookmarkAsync();
            var detail = $"◆ 중요 순간 표시 · {VideoView.App.Controls.ClipTimeline.Time(position)} · " +
                (session.IsRecording ? "저장 후 편집기에서 바로 이동할 수 있습니다." : "최근 구간을 파일로 저장하면 함께 보관됩니다.");
            SetStatus(detail); Notify("중요 순간 표시", detail);
        }
        catch (Exception error) { ReportError(error); }
        finally { _bookmarkBusy = false; }
    }
    private async Task SaveReplayAsync()
    {
        if (_session?.IsRunning != true || !_session.IsBuffering) throw new InvalidOperationException("먼저 최근 구간 보관을 켜 주세요.");
        await _session.SaveReplayAsync();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        if (_preview != null) { StopPreview(); return Task.CompletedTask; }
        if (TargetBox.SelectedItem is not CaptureTarget target) throw new ArgumentException("창 또는 모니터를 먼저 선택해 주세요.");
        _preview = new ScreenCaptureSource(target, hideBorder: HideBorderCheck.IsChecked == true,
            captureCursor: CaptureCursorCheck.IsChecked == true);
        _preview.Faulted += message => Dispatcher.BeginInvoke(() => { SetStatus(message, true); StopPreview(); });
        _preview.Start(); PreviewButton.Content = "미리보기 끄기";
        SetStatus("선택한 화면 미리보기 중 · 파일 저장 안 함");
        return Task.CompletedTask;
    });

    private void StopPreview()
    {
        _preview?.Dispose(); _preview = null;
        if (CapturePreview == null) return;
        _previewBitmap = null; _previewSequence = -1; CapturePreview.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible; PreviewButton.Content = "미리보기";
    }

    private async void AudioTest_Click(object sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        if (_testSources.Count > 0) { StopAudioTest(); return Task.CompletedTask; }
        var settings = ReadSettings();
        if (settings.AudioInputs.Count == 0) throw new ArgumentException("테스트할 음원을 선택해 주세요.");
        foreach (var input in settings.AudioInputs)
        {
            var source = AudioSourceFactory.Create(new(input.Id, input.Name, (AudioSourceKind)input.Kind, input.ProcessId));
            source.Faulted += message => Dispatcher.BeginInvoke(() => { SetStatus(message, true); StopAudioTest(); });
            _testSources.Add(source); source.Start();
        }
        AudioTestButton.Content = "테스트 종료"; SetStatus("입력 레벨 확인 중 · 파일 저장 안 함");
        return Task.CompletedTask;
    });

    private void StopAudioTest()
    { foreach (var source in _testSources) source.Dispose(); _testSources.Clear(); if (AudioTestButton != null) AudioTestButton.Content = "입력 테스트"; }

    private void Tick()
    {
        var running = _session?.IsRunning == true;
        MarkMomentButton.IsEnabled = running && !_bookmarkBusy;
        MarkMomentHint.Text = $"표시 {_session?.BookmarkCount ?? 0}개 · {_settings.BookmarkHotkey}";
        MarkMomentButton.ToolTip = _settings.BookmarkHotkey + " · 녹화 중 중요 순간 표시";
        RecordButton.Content = _session?.IsRecording == true ? "■  녹화 종료" : "●  녹화 시작";
        ReplayToggle.Content = _session?.IsBuffering == true ? "최근 구간 보관 끄기" : "최근 구간 보관 켜기";
        SaveReplayButton.IsEnabled = !_replaySaving && _session?.IsBuffering == true && (_session.ElapsedSeconds > 0.25);
        SessionClock.Text = TimeSpan.FromSeconds(_session?.ElapsedSeconds ?? 0).ToString(@"hh\:mm\:ss");
        BufferTime.Text = TimeSpan.FromSeconds(_session?.BufferedSeconds ?? 0).ToString(@"mm\:ss");
        BufferDescription.Text = _session?.IsBuffering == true ? $"최근 {_settings.ReplaySeconds}초까지 순환 보관 중" : "아직 보관 중이 아닙니다";
        var buffer = _session?.BufferStats;
        var bufferBytes = buffer?.BufferBytes ?? 0;
        var bufferLimit = buffer?.LimitBytes ?? _settings.BufferLimitBytes;
        BufferCapacityText.Text = $"임시 저장 {StorageSize(bufferBytes)} / {StorageSize(bufferLimit)}";
        BufferCapacityBar.Value = Math.Clamp(bufferBytes / (double)Math.Max(1, bufferLimit), 0, 1);
        var storagePressure = bufferBytes > bufferLimit * 0.85;
        BufferCapacityBar.Foreground = (Brush)FindResource(storagePressure ? "Text" : "Accent");
        BufferHealthText.Text = buffer?.PinnedBytes > 0 ? "저장 요청한 구간을 보호하고 있습니다" :
            storagePressure ? "용량이 차면 실제 보관 길이가 줄어들 수 있습니다" :
            running ? "오래된 임시 구간만 정리 · 저장한 클립은 유지" : "저장한 클립은 자동 삭제하지 않습니다";
        BufferHealthText.Foreground = storagePressure ? new SolidColorBrush(Color.FromRgb(230, 184, 101)) : (Brush)FindResource("Muted");
        StatusBadge.Text = running ? (_session!.IsRecording && _session.IsBuffering ? "● 녹화 + 최근 구간 보관 중" :
            _session.IsRecording ? "● 연속 녹화 중" : "● 최근 구간 보관 중") :
            (_preview != null || _testSources.Count > 0 ? "● 입력 확인 중" : "● 준비됨");
        CaptureWarning.Text = _session?.CaptureNotice ?? _preview?.CaptureNotice ?? "";
        CaptureWarning.Visibility = CaptureWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveReplayButton.Content = _replaySaving ? "저장 중… 기록은 계속됩니다" : "방금 순간 저장";
        if (running) RecordingDescription.Text = $"{_session!.EncoderName} · 지연 프레임 {_session.LateFrames:N0} · 반복 {_session.RepeatedFrames:N0} (정지 화면 포함)";
        else RecordingDescription.Text = $"시작부터 끝까지 저장 · {_settings.RecordHotkey}";
        HotkeyHint.Text = $"보관 {_settings.BufferHotkey}  ·  저장 {_settings.ReplayHotkey}";
        if (!running && !_busy && !_captureBusy) SetCaptureControls(true);
        // A hidden recorder or the editor does not need a live desktop bitmap copied at 5 FPS.
        var showLivePreview = IsVisible && WindowState != WindowState.Minimized && HomePage.Visibility == Visibility.Visible;
        var frame = showLivePreview ? _session?.PreviewFrame ?? _preview?.LatestFrame : null;
        if (frame != null && frame.Sequence != _previewSequence)
        {
            if (_previewBitmap == null || _previewBitmap.PixelWidth != frame.Width || _previewBitmap.PixelHeight != frame.Height)
                _previewBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            _previewBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
            CapturePreview.Source = _previewBitmap; PreviewPlaceholder.Visibility = Visibility.Collapsed; _previewSequence = frame.Sequence;
        }
        var levels = running ? _session!.AudioLevels : _testSources.Select(s => (s.Name, s.Peak)).ToArray();
        var microphone = (MicBox.SelectedItem as AudioSourceDescriptor)?.Name;
        MicLevel.Value = levels.FirstOrDefault(l => l.Name == microphone).Peak;
        foreach (var row in _applicationRows) row.Peak = levels.FirstOrDefault(l => l.Name == row.Name).Peak;
        _systemRow.Peak = levels.FirstOrDefault(l => l.Name == _systemRow.Name).Peak;
        TickEditor();
        if (_editDirty && DateTime.UtcNow >= _editSaveAt && !Timeline.IsDragging) FlushEdits();
        RecordButton.ToolTip = _settings.RecordHotkey + " · 녹화 시작 / 종료";
        ReplayToggle.ToolTip = _settings.BufferHotkey + " · 최근 구간 보관 켜기 / 끄기";
        SaveReplayButton.ToolTip = _settings.ReplayHotkey + " · 방금 순간 저장";
    }

    private void SetCaptureControls(bool enabled)
    {
        ModeBox.IsEnabled = enabled; TargetBox.IsEnabled = enabled && ModeBox.SelectedIndex == 1;
        MicBox.IsEnabled = enabled; MicEnabled.IsEnabled = enabled; MicGain.IsEnabled = enabled;
        AudioScopeBox.IsEnabled = enabled; ApplicationsList.IsEnabled = enabled;
        ReplayBox.IsEnabled = enabled; PreviewButton.IsEnabled = enabled && ModeBox.SelectedIndex == 1;
        AudioTestButton.IsEnabled = enabled;
    }

    private void ShowPage(string page)
    {
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSaveHost.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = page == "library" ? Visibility.Collapsed : Visibility.Visible;
        PageHeader.Visibility = page == "library" ? Visibility.Collapsed : Visibility.Visible;
        HomeNav.Background = page == "home" ? new SolidColorBrush(Color.FromRgb(37, 61, 52)) : Brushes.Transparent;
        LibraryNav.Background = page == "library" ? new SolidColorBrush(Color.FromRgb(37, 61, 52)) : Brushes.Transparent;
        SettingsNav.Background = page == "settings" ? new SolidColorBrush(Color.FromRgb(37, 61, 52)) : Brushes.Transparent;
        PageHeading.Text = page == "settings" ? "환경 설정" : "녹화";
        PageDescription.Text = page == "settings" ? "영상 품질 · 저장 위치 · 전역 단축키" : "화면과 소리를 선택하고 기록을 시작하세요. 최근 구간 저장은 버퍼가 켜져 있어야 합니다.";
        if (page != "library") { _playOnOpen = false; PausePlayback(); }
    }
    private void Home_Click(object sender, RoutedEventArgs e) => ShowPage("home");
    private void Library_Click(object sender, RoutedEventArgs e) { ShowPage("library"); RefreshLibrary(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage("settings");
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await GuardAsync(RefreshSourcesAsync);
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_ready) return; StopPreview(); TargetBox.IsEnabled = ModeBox.SelectedIndex == 1; PreviewButton.IsEnabled = ModeBox.SelectedIndex == 1; }
    private void Target_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) StopPreview(); }
    private void AudioScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ApplicationsList == null) return;
        ApplicationsList.Visibility = AudioScopeBox.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
        ApplicationsList.ItemsSource = AudioScopeBox.SelectedIndex == 1 ? new[] { _systemRow } : _applicationRows;
        if (AudioHint != null) AudioHint.Text = AudioScopeBox.SelectedIndex switch
        { 1 => "컴퓨터 전체 소리가 하나의 트랙으로 저장됩니다. 앱별 캡처는 사용하지 않습니다.", 2 => "내 마이크만 선택하거나, 화면만 기록할 수 있습니다.", _ => "앱에서 소리를 재생한 뒤 새로고침하면 목록에 나타납니다." };
    }

    private void RefreshLibrary()
    {
        if (Smoke) return;
        if (_busy) { _libraryRefreshPending = true; return; }
        try
        {
            var library = new RecordingLibrary(_settings.OutputDirectory);
            var recordings = library.List(); _libraryItems = recordings;
            var selectedId = _selected?.Id;
            var selection = recordings.FirstOrDefault(r => r.Id == selectedId);
            ApplyLibraryFilter();
            if (selection == null && (selectedId != null || EditorPanel.IsEnabled)) Recording_Selected(RecordingsList, null!);
            if (library.Warnings.Count > 0) SetStatus($"보관함에서 읽지 못한 항목 {library.Warnings.Count}개. 원본 파일은 보존되었습니다.", true);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }
    private void LibraryRefresh_Click(object sender, RoutedEventArgs e) => RefreshLibrary();
    private async void Rename_Click(object sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        if (_selected != null) _selected.Title = new RecordingLibrary(_settings.OutputDirectory).Rename(_selected, RecordingTitle.Text).Title;
        RefreshLibrary(); return Task.CompletedTask;
    });
    private async void Delete_Click(object sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        if (_selected == null) return Task.CompletedTask;
        if (MessageBox.Show(this, $"‘{_selected.Title}’ 원본과 편집 정보를 영구 삭제합니다. 이미 내보낸 공유 파일은 유지됩니다.", "원본 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return Task.CompletedTask;
        Player.Close(); new RecordingLibrary(_settings.OutputDirectory).Delete(_selected); _editDirty = false; _selected = null; RefreshLibrary(); return Task.CompletedTask;
    });
    private void OpenRecordingFolder_Click(object sender, RoutedEventArgs e) { if (_selected != null) OpenFolder(Path.GetDirectoryName(_selected.MediaPath)!); }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _busy || _shareWindow != null || _closing) return;
        try
        {
            CommitTrimText(); FlushEdits(); _playOnOpen = false; PausePlayback();
            _shareWindow = new ShareWindow(_selected, _playStart, _playEnd, CurrentMix(), Tools(),
                _settings.OutputDirectory, _settings.TargetMegabytes, Smoke) { Owner = this };
            _shareWindow.ShowDialog();
            if (_shareWindow.ExportedPath is { } path && File.Exists(path))
            {
                var size = new FileInfo(path).Length / 1_000_000d;
                ShowSavedFile(path, null, $"공유 파일 완성 · {size:F2} MB · {Path.GetFileName(path)}");
                SetStatus($"공유 파일 저장 완료 · {size:F2} MB");
                _settings.TargetMegabytes = _shareWindow.SelectedTargetMegabytes;
                if (!Smoke) _store.Save(_settings);
            }
        }
        catch (Exception error) { ReportError(error); }
        finally { _shareWindow = null; }
    }
    private void RecordingMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private void RecordingTitle_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        { e.Handled = true; Rename_Click(sender, new RoutedEventArgs()); }
    }


    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        if (_session?.IsRunning == true) throw new InvalidOperationException("설정 저장은 기록을 모두 멈춘 뒤 가능합니다.");
        var settings = ReadSettings(useEditedHotkeys: true);
        if (settings.AutoStartReplay && settings.Mode == CaptureMode.ScreenAndAudio && settings.CaptureTargetId == null)
            throw new ArgumentException("자동 기록에 사용할 화면을 홈에서 먼저 선택해 주세요.");
        _hotkey?.Register(settings);
        try { StartupRegistration.Apply(settings.StartWithWindows); _store.Save(settings); }
        catch { _hotkey?.Register(_settings); throw; }
        if (_settings.FfmpegPath != settings.FfmpegPath || _settings.FfprobePath != settings.FfprobePath)
        {
            _editorCancellation?.Cancel(); _editorCancellation?.Dispose(); _editorCancellation = new();
            PausePlayback(); Player.Close(); ClearPreviewMedia(); _previewCache = null;
        }
        _settings = settings;
        ShortcutFeedback.Text = "단축키 4개 적용 완료 · 다른 프로그램에서도 사용 가능";
        HotkeyHint.Text = settings.ReplayHotkey.Replace("+", " + "); UpdateCaptureInfo(); SetStatus("설정 저장 완료");
        return Task.CompletedTask;
    });

    private async void Recover_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_session?.IsRunning == true) throw new InvalidOperationException("기록을 멈춘 뒤 복구해 주세요.");
        SetStatus("중단된 녹화 복구 중…");
        var settings = ReadSettings();
        var recordings = await RecordingSession.RecoverAsync(settings, FfmpegTools.Resolve(settings.FfmpegPath, settings.FfprobePath),
            warnings: new Progress<string>(message => SetStatus(message, true)));
        _settings = settings; RefreshLibrary(); SetStatus($"복구 완료 · {recordings.Count}개. 읽을 수 없는 조각은 보존됩니다.");
    });

    private void BrowseOutput_Click(object sender, RoutedEventArgs e) => BrowseFolder(OutputPath);
    private void BrowseBuffer_Click(object sender, RoutedEventArgs e) => BrowseFolder(BufferPath);
    private void BrowseFolder(TextBox box)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "저장 폴더 선택", Multiselect = false };
        if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true }); }
    private void UpdateCaptureInfo() { if (CaptureInfo != null) CaptureInfo.Text = $"{(ResolutionBox.SelectedIndex == 0 ? 720 : 1080)}p · {(FpsBox.SelectedIndex == 0 ? 30 : 60)} FPS · {((ComboBoxItem)QualityBox.SelectedItem).Content}"; }
    private static double ReadNumber(string text, string field)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new ArgumentException($"{field}에 숫자를 입력해 주세요.");
        return value;
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy || _captureBusy || _closing) return;
        _busy = true;
        _workCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SetEditorBusy(true);
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("작업 취소됨. 원본은 유지됩니다."); }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            _busy = false; SetEditorBusy(false); _workCompletion.TrySetResult();
            if (_libraryRefreshPending) { _libraryRefreshPending = false; RefreshLibrary(); }
            Tick();
        }
    }
    private async Task GuardCaptureAsync(Func<Task> action)
    {
        if (_captureBusy || _closing) return;
        if (_busy && _session?.IsRunning != true) { SetStatus("현재 작업을 마친 뒤 기록을 시작해 주세요."); return; }
        _captureBusy = true;
        _captureCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("기록 시작이 취소되었습니다."); }
        catch (Exception exception) { ReportError(exception); }
        finally { _captureBusy = false; _captureCompletion.TrySetResult(); Tick(); }
    }
    private async Task GuardReplayAsync()
    {
        if (_replaySaving || _closing) return;
        _replaySaving = true; _replayCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await SaveReplayAsync(); }
        catch (Exception exception) { ReportError(exception); }
        finally { _replaySaving = false; _replayCompletion.TrySetResult(); Tick(); }
    }
    private void SetEditorBusy(bool busy)
    {
        RecordingsList.IsEnabled = !busy; ExportTracks.IsEnabled = !busy;
        TrimStart.IsEnabled = !busy; TrimEnd.IsEnabled = !busy; RecordingTitle.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        Timeline.IsEnabled = !busy;
        ApplyMixButton.IsEnabled = PlayButton.IsEnabled = !busy && !_editorLoading;
    }
    private void ReportError(Exception error)
    { SetStatus(error.Message.Length > 1300 ? error.Message[..1300] : error.Message, true); Notify("작업 실패", error.Message, true); }
    private static string StorageSize(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.#} GB" : $"{bytes / 1_000_000d:0.#} MB";
    private void SetStatus(string text, bool error = false)
    { StatusText.Text = text; StatusText.ToolTip = text; StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(246, 168, 168)) : (Brush)FindResource("Muted"); }
    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon { Text = "VideoView", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("VideoView 열기", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("방금 순간 저장", null, (_, _) => Dispatcher.Invoke(() => _ = GuardReplayAsync()));
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(() => { _exiting = true; Close(); }));
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Notify(string title, string message, bool error = false)
    { if (_tray != null) _tray.ShowBalloonTip(3500, title, message.Length > 240 ? message[..240] : message, error ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info); }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Smoke) { _timer.Stop(); return; }
        if (_cleanupComplete) return;
        if (_closing) { e.Cancel = true; return; }
        if (!_exiting && (_session?.IsRunning == true || _busy || _captureBusy || _replaySaving))
        {
            e.Cancel = true; Hide(); Notify("트레이에서 실행 중", "진행 중인 기록·작업은 계속됩니다. 트레이 메뉴에서 종료할 수 있습니다."); return;
        }
        e.Cancel = true;
        if (!FlushEdits() && MessageBox.Show(this, "편집 정보 저장에 실패했습니다. 저장되지 않은 편집을 버리고 종료할까요? 원본은 유지됩니다.",
            "편집 저장 실패", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _closing = true;
        if (_shareWindow != null) await _shareWindow.RequestCloseAsync();
        _editorCancellation?.Cancel();
        _timer.Stop(); StopPreview(); StopAudioTest(); Player.Close(); ClearPreviewMedia();
        try { await StopSessionAsync(); }
        catch (Exception exception) { MessageBox.Show("녹화 조각을 보존했습니다. 다음 실행에서 복구해 주세요.\n" + exception.Message, "VideoView"); }
        await Task.WhenAll(_workCompletion?.Task ?? Task.CompletedTask, _captureCompletion?.Task ?? Task.CompletedTask, _replayCompletion?.Task ?? Task.CompletedTask);
        await Task.WhenAll(_editorTasks.ToArray());
        _hotkey?.Dispose(); _tray?.Dispose();
        _cleanupComplete = true;
        // Closing cannot be reentered synchronously when there was no active session to await.
        _ = Dispatcher.BeginInvoke(Close);
    }

    public sealed class AudioRow(AudioSourceDescriptor source) : INotifyPropertyChanged
    {
        public AudioSourceDescriptor Source { get; } = source;
        public string Name => Source.Name;
        public int Index { get; set; }
        private bool _enabled;
        private double _gain = 1, _peak;
        public bool Enabled { get => _enabled; set { _enabled = value; Changed(); } }
        public double Gain { get => _gain; set { _gain = value; Changed(); } }
        public double Peak { get => _peak; set { _peak = value; Changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
}
