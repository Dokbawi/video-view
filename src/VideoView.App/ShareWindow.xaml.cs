using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoView.Core;
using VideoView.Core.Media;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace VideoView.App;

/// <summary>A local, silent share flow. Constructor values are snapshotted; owner edits cannot change an export.</summary>
public partial class ShareWindow : Window
{
    private enum ShareStage { Destination, Size, Progress, Ready }

    private readonly RecordingManifest _recording;
    private readonly ExportTrack[] _tracks;
    private readonly FfmpegTools _tools;
    private readonly double _start, _end;
    private readonly bool _smoke;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _thumbnailTasks = [];
    private CancellationTokenSource? _exportCancellation;
    private Task _exportTask = Task.CompletedTask;
    private Task? _closeTask;
    private bool _initialized, _closing, _allowClose, _closed, _audioOnly, _sizeValid = true, _dragging;
    private bool _loaded, _smokeStarted;
    private int _thumbnailGeneration;
    private ShareStage _stage;
    private string _folder;
    private string? _smokeStage, _smokePath;
    private Point? _dragOrigin;
    private BitmapSource? _sourceThumbnail;
    private long _verifiedBytes;

    public string? ExportedPath { get; private set; }
    public int SelectedTargetMegabytes { get; private set; }

    public ShareWindow(RecordingManifest recording, double start, double end, IReadOnlyList<ExportTrack> tracks,
        FfmpegTools tools, string outputDirectory, int defaultTargetMegabytes, bool smoke = false)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(tools);
        if (!double.IsFinite(start) || !double.IsFinite(end) || !double.IsFinite(recording.DurationSeconds) ||
            start < 0 || end <= start || end > recording.DurationSeconds + 0.001)
            throw new ArgumentOutOfRangeException(nameof(start), "공유 구간은 원본 길이 안에서 지정하세요.");
        if (tracks.Any(t => t is null || t.Index < 0 || !double.IsFinite(t.Gain) || t.Gain < 0) ||
            tracks.Select(t => t.Index).Distinct().Count() != tracks.Count)
            throw new ArgumentException("공유 음원과 볼륨을 확인하세요.", nameof(tracks));
        if (!Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("저장 폴더는 절대 경로여야 합니다.", nameof(outputDirectory));
        _recording = new RecordingManifest
        {
            Version = recording.Version, Id = recording.Id, Title = recording.Title,
            MediaPath = recording.MediaPath, CreatedAt = recording.CreatedAt,
            DurationSeconds = recording.DurationSeconds, HasVideo = recording.HasVideo,
            Tracks = recording.Tracks.Select(t => new TrackInfo(t.Index, t.Name)).ToList(),
            Recovered = recording.Recovered, IsFavorite = recording.IsFavorite
        };
        _tracks = tracks.Select(t => new ExportTrack(t.Index, t.Gain, t.Included)).ToArray();
        _tools = new FfmpegTools(tools.FfmpegPath, tools.FfprobePath);
        _start = start; _end = Math.Min(end, recording.DurationSeconds);
        if (_end <= _start) throw new ArgumentOutOfRangeException(nameof(end));
        _smoke = smoke;
        _audioOnly = !recording.HasVideo;
        _folder = Path.Combine(Path.GetFullPath(outputDirectory), "Shared", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        SelectedTargetMegabytes = Math.Clamp(defaultTargetMegabytes, 1, 10_000);
        InitializeComponent();
        ClipTitle.Text = _recording.Title; ClipTitle.ToolTip = _recording.Title;
        ClipRange.Text = $"{Time(_start)} → {Time(_end)}  ·  선택 구간";
        DurationBadge.Text = Time(_end - _start);
        VideoFormat.IsEnabled = _recording.HasVideo;
        VideoFormat.IsChecked = !_audioOnly; AudioFormat.IsChecked = _audioOnly;
        AudioFormat.IsEnabled = _tracks.Any(t => t.Included);
        CustomSize.Text = SelectedTargetMegabytes.ToString(CultureInfo.InvariantCulture);
        SelectSize(SelectedTargetMegabytes);
        UpdateFolder();
        _initialized = true;
        UpdateOptions(); ShowStage(ShareStage.Destination);
        Loaded += Window_Loaded;
        SourceInitialized += (_, _) => FitWorkArea();
        Closing += Window_Closing;
        Closed += (_, _) => { _closed = true; };
    }

    /// <summary>CLI-only visual fixtures. No media is encoded and no synthetic result is exposed as a file.</summary>
    public void ConfigureSmoke(string stage, string absolutePngPath)
    {
        if (!_smoke) throw new InvalidOperationException("화면 검사는 smoke 모드에서만 사용할 수 있습니다.");
        if (stage is not ("destination" or "size" or "progress" or "ready"))
            throw new ArgumentException("Unknown share smoke stage.", nameof(stage));
        if (!Path.IsPathFullyQualified(absolutePngPath) || !string.Equals(Path.GetExtension(absolutePngPath), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("화면 검사 출력은 PNG 절대 경로여야 합니다.", nameof(absolutePngPath));
        if (_smokeStarted || _closing) throw new InvalidOperationException("화면 검사가 이미 시작되었습니다.");
        _smokeStage = stage; _smokePath = Path.GetFullPath(absolutePngPath);
        if (_loaded) _ = RenderSmokeAsync();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _closing) return;
        _loaded = true;
        if (_smoke) { if (_smokePath != null) _ = RenderSmokeAsync(); return; }
        if (_recording.HasVideo)
            _ = QueueThumbnail(_recording.MediaPath, _start + Math.Min(1, (_end - _start) / 2), true, _lifetime.Token);
        else SetPlaceholder("음성 클립", true);
        PrimaryButton.Focus();
    }

    private void FitWorkArea()
    {
        var handle = Owner == null ? new WindowInteropHelper(this).Handle : new WindowInteropHelper(Owner).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var availableWidth = Math.Max(240, area.Width / dpi.DpiScaleX - 24);
        var availableHeight = Math.Max(240, area.Height / dpi.DpiScaleY - 24);
        MinWidth = Math.Min(440, availableWidth); MinHeight = Math.Min(480, availableHeight);
        MaxWidth = availableWidth; MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth); Height = Math.Min(Height, availableHeight);
    }

    private void ShowStage(ShareStage stage)
    {
        _stage = stage;
        DestinationPanel.Visibility = stage == ShareStage.Destination ? Visibility.Visible : Visibility.Collapsed;
        SizePanel.Visibility = stage == ShareStage.Size ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = stage == ShareStage.Progress ? Visibility.Visible : Visibility.Collapsed;
        ReadyPanel.Visibility = stage == ShareStage.Ready ? Visibility.Visible : Visibility.Collapsed;
        ReadyBadge.Visibility = stage == ShareStage.Ready ? Visibility.Visible : Visibility.Collapsed;
        DragSurface.Visibility = stage == ShareStage.Ready ? Visibility.Visible : Visibility.Collapsed;
        DragSurface.IsEnabled = !_smoke && ExportedPath != null;
        ResultSize.Visibility = stage == ShareStage.Ready ? Visibility.Visible : Visibility.Collapsed;
        HeroVisual.Height = stage == ShareStage.Size ? 125 : stage == ShareStage.Ready ? 210 : 182;
        BackButton.IsEnabled = true; PrimaryButton.IsEnabled = true;
        HideError(); ContentScroll.ScrollToTop();
        var discord = DiscordDestination.IsChecked == true;
        switch (stage)
        {
            case ShareStage.Destination:
                Heading.Text = "이 순간을 나누세요"; Subheading.Text = "필요한 구간만, 보내기 편한 크기로.";
                StepLabel.Text = "01 — 공유 대상"; BackButton.Content = "닫기"; PrimaryButton.Content = "계속";
                FooterHint.Text = "01  대상 선택    /    02  크기 설정    /    03  파일 전달";
                break;
            case ShareStage.Size:
                Heading.Text = discord ? "Discord에 보낼 준비" : "내 클립 만들기";
                Subheading.Text = "크기를 고르면 선택한 구간과 소리를 담아 만듭니다.";
                StepLabel.Text = "02 — 크기 설정"; BackButton.Content = "뒤로";
                UpdateOptions();
                FooterHint.Text = "원본 보존 · 선택 구간과 현재 음량 적용";
                break;
            case ShareStage.Progress:
                Heading.Text = "순간을 담는 중"; Subheading.Text = "크기 상한에 맞춰 파일을 만들고 확인합니다.";
                StepLabel.Text = "02 — 파일 생성"; BackButton.Content = "닫기"; PrimaryButton.Content = "생성 취소";
                FooterHint.Text = "창을 닫아도 취소와 임시 파일 정리를 마친 뒤 종료합니다.";
                break;
            case ShareStage.Ready:
                Heading.Text = "보낼 준비가 됐어요"; Subheading.Text = "공유 파일을 내 컴퓨터에 저장했습니다.";
                StepLabel.Text = "03 — 파일 전달"; BackButton.Content = "닫기"; PrimaryButton.Content = "파일 복사";
                ReadyInstruction.Text = discord ? "위 클립을 채팅에 끌어놓으세요" : "원하는 곳에 클립을 끌어놓으세요";
                ReadyHelp.Text = "파일 복사 후 Ctrl+V로 붙여넣어도 됩니다. 전송 전 첨부 내용을 확인하세요.";
                FooterHint.Text = "파일 생성 완료 · 첨부와 전송은 직접 진행하세요.";
                OpenFolderButton.IsEnabled = !_smoke && ExportedPath != null;
                PrimaryButton.IsEnabled = !_smoke && ExportedPath != null;
                break;
        }
        AutomationProperties.SetName(PrimaryButton, PrimaryButton.Content?.ToString() ?? "공유 동작");
        AutomationProperties.SetName(BackButton, BackButton.Content?.ToString() ?? "이전 단계");
    }

    private void Destination_Changed(object sender, RoutedEventArgs e)
    { if (_initialized) UpdateOptions(); }

    private void SelectSize(int size)
    {
        var selected = size switch { 10 => Size10, 20 => Size20, 50 => Size50, 100 => Size100, _ => SizeCustom };
        selected.IsChecked = true;
        CustomSizeRow.Visibility = selected == SizeCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Size_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        CustomSizeRow.Visibility = SizeCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var value))
            SelectedTargetMegabytes = value;
        UpdateOptions();
    }

    private void CustomSize_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    { if (_initialized) UpdateOptions(); }

    private void Format_Changed(object sender, RoutedEventArgs e)
    { if (_initialized) { _audioOnly = AudioFormat.IsChecked == true; UpdateOptions(); } }

    private void UpdateOptions()
    {
        if (!_initialized) return;
        _sizeValid = SizeCustom.IsChecked != true || (int.TryParse(CustomSize.Text.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= 10_000);
        if (_sizeValid && SizeCustom.IsChecked == true) SelectedTargetMegabytes = int.Parse(CustomSize.Text.Trim(), CultureInfo.InvariantCulture);
        SizeValidation.Visibility = _sizeValid ? Visibility.Collapsed : Visibility.Visible;
        var included = _tracks.Where(t => t.Included).ToArray();
        var trackNames = included.Select(t =>
            $"{_recording.Tracks.FirstOrDefault(info => info.Index == t.Index)?.Name ?? $"음원 {t.Index + 1}"} {t.Gain * 100:0}%");
        MixSummary.Text = included.Length == 0 ? "음원 제외 · 무음 영상" : $"{included.Length}개 음원 믹스 · {string.Join(" / ", trackNames)}";
        var rate = (SelectedTargetMegabytes * 1_000_000d * .94 - 16_384) * 8 / (_end - _start);
        var audioRate = included.Length == 0 ? 0 : Math.Min(192_000, Math.Max(24_000, rate * (_audioOnly ? 1 : .2)));
        var videoRate = Math.Min(40_000_000, rate - audioRate);
        var feasible = _audioOnly ? included.Length > 0 && rate >= 24_000 : videoRate >= 32_000;
        QualityGuidance.Text = !_sizeValid ? "크기를 입력하면 예상 품질을 확인할 수 있습니다." : _audioOnly && included.Length == 0
            ? "음성 파일에는 음원이 하나 이상 필요합니다. 편집기에서 포함할 음원을 선택하세요." : !feasible
            ? "이 길이에는 용량이 부족합니다. 크기를 늘리거나 편집기에서 구간을 줄여주세요."
            : _audioOnly ? $"AAC 음성 · 최대 {audioRate / 1000:0} kbps · 실제 용량은 더 작을 수 있어요."
            : $"영상 예산 약 {videoRate / 1_000_000:0.##} Mbps · " + (videoRate < 1_500_000
                ? "빠른 장면은 뭉개질 수 있어요. 용량을 늘리면 선명해집니다."
                : videoRate < 5_000_000 ? "움직임이 많은 영상은 더 큰 용량을 권장해요." : "원본 해상도·프레임 유지. 실제 품질은 장면에 따라 달라요.");
        DestinationNote.Text = DiscordDestination.IsChecked == true
            ? "Discord 업로드 한도는 계정·서버에 따라 달라집니다. 사용 가능한 한도에 맞춰 선택하세요."
            : "선택한 크기 이하로 별도 파일을 만듭니다. 원본 영상과 분리 음원은 그대로 보관됩니다.";
        if (_stage == ShareStage.Size)
        {
            PrimaryButton.Content = $"{(_audioOnly ? "M4A" : "MP4")} 파일 만들기";
            PrimaryButton.IsEnabled = _sizeValid && feasible && !_closing;
            AutomationProperties.SetName(PrimaryButton, PrimaryButton.Content.ToString());
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _smoke) return;
        if (_stage == ShareStage.Destination) { ShowStage(ShareStage.Size); return; }
        if (_stage == ShareStage.Ready) { CopyFile(); return; }
        if (_stage == ShareStage.Progress) { await CancelExportAsync(); return; }
        UpdateOptions();
        if (!PrimaryButton.IsEnabled || !_exportTask.IsCompleted) return;
        _exportTask = ExportAsync();
        await _exportTask;
    }

    private async Task ExportAsync()
    {
        // Yield before starting work so close/cancel always sees the assigned operation task.
        await Task.Yield();
        if (_closing) return;
        ExportedPath = null; _verifiedBytes = 0;
        _exportCancellation?.Dispose();
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellation = _exportCancellation;
        var target = SelectedTargetMegabytes; var audioOnly = _audioOnly;
        string? generatedPath = null;
        ShowStage(ShareStage.Progress); ExportProgress.Value = 0; ProgressPercent.Text = "0%";
        ProgressDetail.Text = $"{Time(_end - _start)} · {(audioOnly ? "M4A" : "MP4")} · 최대 {target:N0} MB";
        try
        {
            var path = NewOutputPath(audioOnly);
            var request = new ExportRequest(_recording, _start, _end, audioOnly, target, _tracks, path);
            var progress = new Progress<double>(value =>
            {
                if (_closing || cancellation.IsCancellationRequested || _stage != ShareStage.Progress) return;
                var percent = Math.Min(99, (int)Math.Floor(Math.Clamp(value, 0, 1) * 100));
                ExportProgress.Value = percent; ProgressPercent.Text = $"{percent}%";
                if (value >= .99) ProgressDetail.Text = "파일 크기와 재생 정보를 확인하는 중…";
            });
            var result = await new ExportService(_tools).ExportAsync(request, progress, cancellation.Token);
            generatedPath = result;
            var file = new FileInfo(result);
            if (!file.Exists || file.Length <= 0 || file.Length > (long)target * 1_000_000)
                throw new IOException("완성 파일의 크기를 확인할 수 없습니다.");
            // The encoder has published an immutable result. Verification must also succeed before it is offered for sharing.
            var info = await _tools.ProbeAsync(result, cancellation.Token);
            if ((!audioOnly && !info.HasVideo) || (audioOnly && info.Tracks.Count == 0) || info.DurationSeconds <= 0 ||
                Math.Abs(info.DurationSeconds - (_end - _start)) > Math.Max(.3, info.FramesPerSecond > 0 ? 2 / info.FramesPerSecond : 0))
                throw new IOException("생성한 파일의 재생 구간을 확인하지 못했습니다. 다시 만들어주세요.");
            if (!audioOnly) await QueueThumbnail(result, Math.Min(.5, info.DurationSeconds / 2), false, cancellation.Token);
            else { ++_thumbnailGeneration; ThumbnailImage.Source = null; SetPlaceholder("음성 클립 · M4A", true); }
            cancellation.Token.ThrowIfCancellationRequested();
            ExportedPath = result; _verifiedBytes = file.Length;
            if (_closing) return;
            ClipTitle.Text = Path.GetFileName(result); ClipTitle.ToolTip = result;
            ClipRange.Text = $"{Time(_end - _start)}  ·  {(audioOnly ? "M4A · AAC" : "MP4 · H.264")}  ·  최대 {target:N0} MB";
            ResultSize.Text = $"{file.Length / 1_000_000d:0.##} MB  ·  {file.Length:N0} bytes 확인";
            ExportProgress.Value = 100; ProgressPercent.Text = "100%";
            ShowStage(ShareStage.Ready); PrimaryButton.Focus();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closing) { RestoreSourceCard(); ShowStage(ShareStage.Size); FooterHint.Text = "생성 취소됨 · 다시 만들 수 있습니다."; }
        }
        catch (Exception error)
        {
            if (!_closing)
            {
                RestoreSourceCard(); ShowStage(ShareStage.Size);
                ShowError("파일을 완성하지 못했습니다. 설정이나 저장 공간을 확인한 뒤 다시 시도하세요.", error);
                PrimaryButton.Content = "다시 만들기";
            }
        }
        finally
        {
            // A cancellation or failed verification after publication must not leave an unreported result.
            // This path was uniquely created by this export; existing files and the source are never removed.
            if (generatedPath != null && ExportedPath != generatedPath)
                await DeleteOwnedFileAsync(generatedPath);
        }
    }

    private string NewOutputPath(bool audioOnly)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(_recording.Title.Where(c => !invalid.Contains(c) && !char.IsControl(c)).Take(70).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "VideoView";
        // Prefix also avoids reserved Windows device names. ExportService atomically refuses any collision.
        return Path.Combine(_folder, $"Clip_{stem}_{DateTime.Now:HHmmss}_{Guid.NewGuid():N}" + (audioOnly ? ".m4a" : ".mp4"));
    }

    private async Task CancelExportAsync()
    {
        if (_exportTask.IsCompleted) return;
        PrimaryButton.IsEnabled = false; PrimaryButton.Content = "취소 중…";
        ProgressDetail.Text = "작업을 멈추고 임시 파일을 정리하는 중…";
        _exportCancellation?.Cancel();
        await _exportTask;
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_stage == ShareStage.Size) { ShowStage(ShareStage.Destination); return; }
        await RequestCloseAsync();
    }

    private void ChangeSize_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        ExportedPath = null; _verifiedBytes = 0;
        RestoreSourceCard(); ShowStage(ShareStage.Size);
        FooterHint.Text = "앞서 만든 파일은 보관됩니다. 새 파일을 별도로 만듭니다.";
    }

    private void RestoreSourceCard()
    {
        ClipTitle.Text = _recording.Title; ClipTitle.ToolTip = _recording.Title;
        ClipRange.Text = $"{Time(_start)} → {Time(_end)}  ·  선택 구간";
        ThumbnailImage.Source = _sourceThumbnail;
        ThumbnailPlaceholder.Visibility = _sourceThumbnail == null ? Visibility.Visible : Visibility.Collapsed;
        if (_sourceThumbnail == null) SetPlaceholder(_recording.HasVideo ? "선택 구간 미리보기" : "음성 클립", !_recording.HasVideo);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _smoke) return;
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "공유 파일 저장 폴더", Multiselect = false };
            if (Directory.Exists(_folder)) picker.InitialDirectory = _folder;
            if (picker.ShowDialog(this) != true) return;
            _folder = Path.GetFullPath(picker.FolderName); UpdateFolder(); HideError();
        }
        catch (Exception error) { ShowError("폴더를 선택하지 못했습니다.", error); }
    }

    private void UpdateFolder() { FolderLabel.Text = _folder; FolderLabel.ToolTip = _folder; }

    private bool TryGetFinishedFile(out string path)
    {
        path = "";
        if (_smoke || _closing || _stage != ShareStage.Ready || ExportedPath == null) return false;
        try
        {
            var file = new FileInfo(ExportedPath);
            if (!file.Exists || file.Length != _verifiedBytes || file.Length <= 0)
                throw new IOException("완성 파일이 이동·삭제되었거나 변경되었습니다.");
            path = file.FullName; return true;
        }
        catch (Exception error)
        {
            ExportedPath = null; DragSurface.IsEnabled = false; PrimaryButton.IsEnabled = false; OpenFolderButton.IsEnabled = false;
            ReadyBadge.Visibility = Visibility.Collapsed;
            ShowError("파일을 찾거나 확인할 수 없습니다. ‘다른 크기로 만들기’에서 다시 생성하세요.", error);
            return false;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) { if (!_dragging) CopyFile(); }

    private void CopyFile()
    {
        if (!TryGetFinishedFile(out var path)) return;
        try
        {
            var files = new StringCollection { path };
            Clipboard.SetFileDropList(files);
            HideError(); FooterHint.Text = "파일 복사됨 · 원하는 채팅에서 Ctrl+V로 붙여넣으세요.";
        }
        catch (Exception error) { ShowError("파일을 복사하지 못했습니다. 다시 누르거나 클립을 끌어놓으세요.", error); }
    }

    private void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    { if (!_smoke && !_closing) _dragOrigin = e.GetPosition(DragSurface); }

    private void Drag_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragOrigin = null; return; }
        if (_dragging || _dragOrigin is not Point origin) return;
        var point = e.GetPosition(DragSurface);
        if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragOrigin = null;
        if (!TryGetFinishedFile(out var path)) return;
        try
        {
            _dragging = true; DragSurface.ReleaseMouseCapture();
            var data = new DataObject(); data.SetData(DataFormats.FileDrop, new[] { path });
            System.Windows.DragDrop.DoDragDrop(DragSurface, data, DragDropEffects.Copy);
            if (!_closing) FooterHint.Text = "첨부한 곳에서 파일을 확인한 뒤 전송하세요.";
            e.Handled = true;
        }
        catch (Exception error) { if (!_closing) ShowError("끌어놓기를 시작하지 못했습니다. 파일 복사를 이용하세요.", error); }
        finally { _dragging = false; }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetFinishedFile(out var path)) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path);
            Process.Start(start); HideError();
        }
        catch (Exception error) { ShowError("저장 폴더를 열지 못했습니다.", error); }
    }

    private Task QueueThumbnail(string path, double position, bool source, CancellationToken cancellationToken)
    {
        var generation = ++_thumbnailGeneration;
        var task = LoadThumbnailAsync(path, position, source, generation, cancellationToken);
        _thumbnailTasks.Add(task);
        _ = ForgetThumbnailAsync(task);
        return task;
    }

    private async Task ForgetThumbnailAsync(Task task)
    { try { await task; } finally { _thumbnailTasks.Remove(task); } }

    private async Task LoadThumbnailAsync(string path, double position, bool source, int generation, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"VideoView-share-{Guid.NewGuid():N}.png");
        try
        {
            await _tools.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-n", "-threads", "1", "-filter_threads", "1",
                "-ss", position.ToString("0.#########", CultureInfo.InvariantCulture), "-accurate_seek", "-i", path,
                "-map", "0:V:0", "-an", "-sn", "-dn", "-frames:v", "1", "-vf",
                "scale=w='min(960,iw)':h='min(540,ih)':force_original_aspect_ratio=decrease,setsar=1",
                "-c:v", "png", "-threads", "1", "-f", "image2", temporary], cancellationToken);
            var bitmap = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = File.OpenRead(temporary);
                var frame = new BitmapImage(); frame.BeginInit(); frame.CacheOption = BitmapCacheOption.OnLoad;
                frame.StreamSource = input; frame.EndInit(); frame.Freeze(); return frame;
            }, cancellationToken);
            if (source) _sourceThumbnail = bitmap;
            if (_closing || cancellationToken.IsCancellationRequested || generation != _thumbnailGeneration) return;
            ThumbnailImage.Source = bitmap; ThumbnailPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closing && generation == _thumbnailGeneration)
            { ThumbnailImage.Source = null; SetPlaceholder("정지 이미지를 불러오지 못했어요", false); }
        }
        finally
        {
            // Delete only this operation's unique still, after the encoder and decoder have released it.
            await DeleteOwnedFileAsync(temporary);
        }
    }

    private static async Task DeleteOwnedFileAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { if (attempt < 4) await Task.Delay(40 * (attempt + 1)); }
        }
    }

    private void SetPlaceholder(string label, bool audio)
    {
        PlaceholderGlyph.Text = audio ? "\uE8D6" : "\uE714";
        PlaceholderLabel.Text = label; ThumbnailPlaceholder.Visibility = Visibility.Visible;
    }

    private void HideError() { ErrorPanel.Visibility = Visibility.Collapsed; ErrorDetailsExpander.IsExpanded = false; }

    private void ShowError(string message, Exception error)
    {
        ErrorText.Text = message;
        ErrorDetails.Text = error.Message.Length <= 2000 ? error.Message : error.Message[..2000];
        ErrorPanel.Visibility = Visibility.Visible; FooterHint.Text = message;
        ErrorPanel.BringIntoView();
    }

    /// <summary>Cancels and awaits owned media work before closing. Safe to call repeatedly from owner shutdown.</summary>
    public Task RequestCloseAsync()
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.InvokeAsync(RequestCloseAsync).Task.Unwrap();
        if (_closed) return Task.CompletedTask;
        _closing = true;
        return _closeTask ??= CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        await Task.Yield();
        _closing = true; _lifetime.Cancel(); _exportCancellation?.Cancel();
        PrimaryButton.IsEnabled = false; BackButton.IsEnabled = false; DragSurface.IsEnabled = false;
        FooterHint.Text = "작업 정리 중…";
        try { await _exportTask; }
        catch (Exception) { /* Export errors are already inline; still await thumbnail cleanup. */ }
        try { await Task.WhenAll(_thumbnailTasks.ToArray()); }
        catch (Exception) { /* Each thumbnail owns its temporary file cleanup. */ }
        _exportCancellation?.Dispose(); _lifetime.Dispose();
        ThumbnailImage.Source = null; _sourceThumbnail = null;
        _allowClose = true;
        if (!_closed) Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        await RequestCloseAsync();
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; await RequestCloseAsync(); }
    }

    private async Task RenderSmokeAsync()
    {
        if (_smokeStarted || _smokePath == null || _closing) return;
        _smokeStarted = true;
        try
        {
            SmokeBanner.Visibility = Visibility.Visible;
            SetPlaceholder(_recording.HasVideo ? "CLI 화면 검사 · 영상 자리" : "CLI 화면 검사 · 음성 클립", !_recording.HasVideo);
            ShowStage(_smokeStage switch { "size" => ShareStage.Size, "progress" => ShareStage.Progress, "ready" => ShareStage.Ready, _ => ShareStage.Destination });
            if (_stage == ShareStage.Progress)
            { ExportProgress.Value = 64; ProgressPercent.Text = "64%"; ProgressDetail.Text = "CLI 예시 진행률 · 실제 인코딩 없음"; }
            if (_stage == ShareStage.Ready)
            {
                Subheading.Text = "CLI 화면 검사에서만 사용하는 완료 화면 예시입니다.";
                ResultSize.Text = "7.42 MB · 예시 값 · 실제 파일 없음";
                ReadyBadge.Visibility = Visibility.Collapsed;
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Task.Delay(120, _lifetime.Token);
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth), (int)Math.Ceiling(Root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(_smokePath)!);
            using (var file = File.Create(_smokePath)) png.Save(file);
            var footer = Footer.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, Footer.ActualWidth, Footer.ActualHeight));
            await File.WriteAllTextAsync(_smokePath + ".layout.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                Stage = _smokeStage, Width = Root.ActualWidth, Height = Root.ActualHeight,
                FooterVisible = footer.Top >= 0 && footer.Bottom <= Root.ActualHeight + 1,
                ScrollViewportHeight = ContentScroll.ViewportHeight, ContentHeight = ContentScroll.ExtentHeight,
                Synthetic = true, ExportedPath
            }), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_closing || _lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Make failed CLI rendering visible to the harness without throwing on the dispatcher.
            if (_smokePath != null) try { await File.WriteAllTextAsync(_smokePath + ".error.txt", error.ToString()); } catch (IOException) { }
        }
        finally { await RequestCloseAsync(); }
    }

    private static string Time(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }
}
