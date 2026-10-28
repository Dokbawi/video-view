namespace VideoView.Core;

public enum CaptureMode { AudioOnly, ScreenAndAudio }
public enum AudioKind { Microphone, System, Application }
public enum Quality { Low, Standard, High }

public sealed record AudioInput(string Id, string Name, AudioKind Kind, int? ProcessId = null)
{
    public float Gain { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

public sealed record TrackInfo(int Index, string Name);
public sealed record SegmentInfo(string Path, double StartSeconds, double EndSeconds, long Bytes)
{
    public double Duration => EndSeconds - StartSeconds;
}

public sealed class RecordingManifest
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "새 녹화";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string MediaPath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public bool HasVideo { get; set; }
    public List<TrackInfo> Tracks { get; set; } = [];
    public bool Recovered { get; set; }
    public bool IsFavorite { get; set; }
    public RecordingEdits? Edits { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoView");
    public string BufferDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoView", "Buffer");
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public CaptureMode Mode { get; set; } = CaptureMode.ScreenAndAudio;
    public int Height { get; set; } = 1080;
    public int FramesPerSecond { get; set; } = 30;
    public Quality Quality { get; set; } = Quality.Standard;
    public int ReplaySeconds { get; set; } = 60;
    public long BufferLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int TargetMegabytes { get; set; } = 20;
    public bool StartWithWindows { get; set; }
    public bool AutoStartReplay { get; set; }
    public bool HideCaptureBorder { get; set; } = true;
    public bool CaptureCursor { get; set; } = true;
    public string ReplayHotkey { get; set; } = "Ctrl+Shift+F8";
    public string RecordHotkey { get; set; } = "Ctrl+Shift+F9";
    public string BufferHotkey { get; set; } = "Ctrl+Shift+F7";
    public string BookmarkHotkey { get; set; } = "Ctrl+Shift+F6";
    public List<AudioInput> AudioInputs { get; set; } = [];
    public string? CaptureTargetId { get; set; }
    public string? CaptureTargetName { get; set; }
    public void Validate()
    {
        if (Height is not (720 or 1080)) throw new ArgumentException("해상도는 720p 또는 1080p여야 함.");
        if (FramesPerSecond is not (30 or 60)) throw new ArgumentException("프레임은 30 또는 60이어야 함.");
        if (ReplaySeconds is not (30 or 60 or 180 or 300)) throw new ArgumentException("최근 구간은 30초·1분·3분·5분 중 선택.");
        if (BufferLimitBytes < 256L * 1024 * 1024 || BufferLimitBytes > 100L * 1024 * 1024 * 1024) throw new ArgumentException("임시 저장 용량은 256MB~100GB.");
        if (TargetMegabytes < 1 || TargetMegabytes > 10000) throw new ArgumentException("공유 용량은 1~10000MB.");
        if (!Path.IsPathFullyQualified(OutputDirectory) || !Path.IsPathFullyQualified(BufferDirectory)) throw new ArgumentException("저장 위치는 절대 경로가 필요함.");
        var enabled = AudioInputs.Where(a => a.Enabled).ToArray();
        if (enabled.Count(a => a.Kind == AudioKind.Microphone) > 1) throw new ArgumentException("마이크는 하나만 선택 가능.");
        if (enabled.Any(a => a.Kind == AudioKind.System) && enabled.Any(a => a.Kind == AudioKind.Application)) throw new ArgumentException("컴퓨터 전체 소리와 앱별 소리는 동시에 선택할 수 없음.");
        if (enabled.GroupBy(a => a.Id).Any(g => g.Count() > 1)) throw new ArgumentException("같은 음원을 중복 선택할 수 없음.");
        if (enabled.Any(a => !float.IsFinite(a.Gain) || a.Gain is < 0 or > 2)) throw new ArgumentException("입력 볼륨은 0~200%.");
    }
}

public sealed record ExportTrack(int Index, double Gain, bool Included = true);
public sealed record ExportRequest(RecordingManifest Recording, double StartSeconds, double EndSeconds,
    bool AudioOnly, int TargetMegabytes, IReadOnlyList<ExportTrack> Tracks, string OutputPath);
public sealed record MediaInfo(double DurationSeconds, bool HasVideo, IReadOnlyList<TrackInfo> Tracks,
    int Width = 0, int Height = 0, double FramesPerSecond = 0);
