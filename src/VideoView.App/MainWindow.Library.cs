using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using VideoView.App.Controls;
using VideoView.Core;
using VideoView.Core.Library;

namespace VideoView.App;

public partial class MainWindow
{
    private IReadOnlyList<RecordingManifest> _libraryItems = [];
    private readonly List<double> _bookmarks = [];
    private bool _restoringEdits, _editDirty;
    private DateTime _editSaveAt;
    private string? _lastSavedPath, _lastSavedId;

    private void ApplyLibraryFilter()
    {
        if (!_ready) return;
        var query = LibrarySearch.Text.Trim();
        var filter = LibraryFilter.SelectedIndex;
        var items = _libraryItems.Where(r =>
            (filter != 1 || r.IsFavorite) && (filter != 2 || r.HasVideo) && (filter != 3 || !r.HasVideo) &&
            (query.Length == 0 || r.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             r.CreatedAt.ToString("yyyy-MM-dd HH:mm").Contains(query) ||
             r.Tracks.Any(t => t.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))))
            .ToArray();
        _refreshingLibrary = true;
        try { RecordingsList.ItemsSource = items; RecordingsList.SelectedItem = items.FirstOrDefault(r => r.Id == _selected?.Id); }
        finally { _refreshingLibrary = false; }
        LibraryCount.Text = $"{items.Length}개 / 전체 {_libraryItems.Count}개";
        LibraryEmpty.Text = _libraryItems.Count == 0 ? "아직 저장된 기록이 없습니다.\n녹화하거나 방금 순간을 저장해 보세요." : "검색 결과가 없습니다.\n검색어나 필터를 바꿔 보세요.";
        LibraryEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void LibrarySearch_Changed(object sender, TextChangedEventArgs e) => ApplyLibraryFilter();
    private void LibraryFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyLibraryFilter();
    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_settings.OutputDirectory);
    private void QualityPreset_Click(object sender, RoutedEventArgs e)
    {
        var preset = ((Button)sender).Tag as string;
        ResolutionBox.SelectedIndex = preset == "light" ? 0 : 1;
        FpsBox.SelectedIndex = preset == "game" ? 1 : 0;
        QualityBox.SelectedIndex = preset == "game" ? 2 : 1;
        UpdateCaptureInfo(); SetStatus("화질 프리셋 선택됨 · 설정 저장을 누르면 다음 기록부터 적용됩니다.");
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        try
        {
            var favorite = !_selected.IsFavorite;
            if (!Smoke) new RecordingLibrary(_settings.OutputDirectory).SetFavorite(_selected.Id, favorite);
            _selected.IsFavorite = favorite;
            FavoriteButton.Content = favorite ? "★" : "☆";
            FavoriteButton.ToolTip = favorite ? "즐겨찾기 해제" : "즐겨찾기 추가";
            RefreshLibrary();
        }
        catch (Exception error) { ReportError(error); }
    }

    private void RestoreEdits(RecordingManifest recording)
    {
        _restoringEdits = true;
        try
        {
            _bookmarks.Clear();
            var edits = recording.Edits?.Normalize(recording);
            if (edits != null)
            {
                Timeline.SetRange(edits.Start, edits.End); Timeline.Position = edits.Position;
                _bookmarks.AddRange(edits.Bookmarks);
                foreach (var row in _exportRows)
                {
                    var track = edits.Tracks.FirstOrDefault(t => t.Index == row.Index);
                    if (track != null) { row.Gain = track.Gain; row.Enabled = track.Included; }
                }
            }
            SyncTrimRange(); RefreshBookmarks();
            FavoriteButton.Content = recording.IsFavorite ? "★" : "☆";
            FavoriteButton.ToolTip = recording.IsFavorite ? "즐겨찾기 해제" : "즐겨찾기 추가";
            DraftStatus.Text = edits == null ? "편집 내용 자동 저장" : "저장한 편집 불러옴";
            _editDirty = false;
        }
        finally { _restoringEdits = false; }
    }
    private void QueueEditSave()
    {
        if (_restoringEdits || _selected == null || Smoke || _closing) return;
        _editDirty = true; _editSaveAt = DateTime.UtcNow.AddMilliseconds(650);
        DraftStatus.Text = "편집 저장 중…";
    }
    private bool FlushEdits()
    {
        if (!_editDirty || _selected == null || Smoke) return true;
        try
        {
            var edits = new RecordingEdits { Start = Timeline.SelectionStart, End = Timeline.SelectionEnd,
                Position = Timeline.Position, Tracks = CurrentMix().ToList(), Bookmarks = _bookmarks.ToList() }.Normalize(_selected);
            new RecordingLibrary(_settings.OutputDirectory).SaveEdits(_selected.Id, edits);
            _selected.Edits = edits; _editDirty = false; DraftStatus.Text = "편집 저장됨 · 원본 유지";
            var listed = _libraryItems.FirstOrDefault(r => r.Id == _selected.Id);
            if (listed != null) listed.Edits = edits;
            return true;
        }
        catch (Exception error)
        {
            // Keep edits dirty for an explicit retry; don't emit the same failure every timer tick.
            _editSaveAt = DateTime.MaxValue;
            DraftStatus.Text = "편집 저장 실패 · 재시도";
            SetStatus("편집 정보를 저장하지 못했습니다. " + error.Message, true);
            return false;
        }
    }
    private void SaveDraft_Click(object sender, RoutedEventArgs e) => FlushEdits();
    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _busy) return;
        var position = Math.Round(Timeline.Position, 3);
        var existing = _bookmarks.FindIndex(p => Math.Abs(p - position) < 0.15);
        if (existing >= 0) _bookmarks.RemoveAt(existing);
        else if (_bookmarks.Count < 500) { _bookmarks.Add(position); _bookmarks.Sort(); }
        else { SetStatus("중요 지점은 파일당 최대 500개입니다."); return; }
        RefreshBookmarks(); QueueEditSave();
    }
    private void RefreshBookmarks()
    {
        Timeline.Bookmarks = _bookmarks.ToArray();
        BookmarkList.ItemsSource = _bookmarks.Select(p => new BookmarkItem(p, "◆ " + ClipTimeline.Time(p))).ToArray();
        BookmarkList.SelectedIndex = -1;
        PreviousBookmark.IsEnabled = NextBookmark.IsEnabled = BookmarkList.IsEnabled = _bookmarks.Count > 0;
        BookmarkCount.Text = _bookmarks.Count == 0 ? "중요 지점" : $"중요 지점 {_bookmarks.Count}";
    }
    private void Bookmark_Selected(object sender, SelectionChangedEventArgs e)
    { if (BookmarkList.SelectedItem is BookmarkItem item) { SeekTo(item.Position); Timeline.Zoom(1, item.Position); } }
    private void PreviousBookmark_Click(object sender, RoutedEventArgs e) => NavigateBookmark(false);
    private void NextBookmark_Click(object sender, RoutedEventArgs e) => NavigateBookmark(true);
    private void NavigateBookmark(bool next)
    {
        if (_bookmarks.Count == 0 || _busy) return;
        var position = next ? _bookmarks.FirstOrDefault(p => p > Timeline.Position + 0.05, _bookmarks[0]) :
            _bookmarks.LastOrDefault(p => p < Timeline.Position - 0.05, _bookmarks[^1]);
        BookmarkList.SelectedIndex = _bookmarks.IndexOf(position);
        SeekTo(position); Timeline.Zoom(1, position);
    }
    private sealed record BookmarkItem(double Position, string Label);

    private void ShowSavedFile(string path, string? recordingId, string detail)
    {
        _lastSavedPath = path; _lastSavedId = recordingId;
        SavedFileTitle.Text = detail; SavedFileTitle.ToolTip = path;
        SavedOpenButton.Content = recordingId != null ? "편집 열기" : "폴더 열기";
        CopySavedButton.Visibility = recordingId == null ? Visibility.Visible : Visibility.Collapsed;
        SavedFileBanner.Visibility = Visibility.Visible;
    }
    private void SavedOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _lastSavedPath == null) return;
        if (_lastSavedId == null) { OpenFolder(Path.GetDirectoryName(_lastSavedPath)!); return; }
        ShowPage("library"); LibrarySearch.Clear(); LibraryFilter.SelectedIndex = 0; RefreshLibrary();
        var recording = _libraryItems.FirstOrDefault(r => r.Id == _lastSavedId);
        if (recording != null) RecordingsList.SelectedItem = recording;
        else SetStatus("저장된 원본을 찾지 못했습니다. 저장 폴더를 확인해 주세요.", true);
    }
    private void CopySavedFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastSavedPath == null || !File.Exists(_lastSavedPath)) throw new FileNotFoundException("저장한 파일을 찾지 못했습니다.");
            if (_lastSavedId != null)
            {
                SetStatus("원본은 음원이 분리되어 있습니다. 편집에서 MP4/M4A로 내보낸 뒤 공유해 주세요.");
                return;
            }
            var files = new StringCollection { _lastSavedPath };
            System.Windows.Clipboard.SetFileDropList(files);
            SetStatus("파일 복사됨 · 디스코드 대화창 등에 붙여넣을 수 있습니다.");
        }
        catch (Exception error) { ReportError(error); }
    }
    private void DismissSaved_Click(object sender, RoutedEventArgs e) => SavedFileBanner.Visibility = Visibility.Collapsed;
}
