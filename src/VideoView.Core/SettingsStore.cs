using System.Text.Json;

namespace VideoView.Core;

public sealed class SettingsStore(string? path = null)
{
    public string FilePath { get; } = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoView", "settings.json");
    public string? LoadWarning { get; private set; }
    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            settings.Validate();
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException)
        {
            LoadWarning = "설정을 읽지 못해 기본값을 사용함. 기존 설정 파일은 보존됨. " + e.Message;
            return new();
        }
    }
    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
