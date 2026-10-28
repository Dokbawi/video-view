using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoView.Core.Media;

/// <summary>A disposable pin keeps a cached slice alive while a player is reading it.</summary>
public sealed class PreviewSlice(string path, double start, double end, bool cacheHit, Action release) : IDisposable
{
    private Action? _release = release;
    public string Path { get; } = path;
    public double Start { get; } = start;
    public double End { get; } = end;
    public bool CacheHit { get; } = cacheHit;
    public bool Contains(double seconds) => seconds >= Start && seconds < End;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>Bounded, source-versioned preview windows and reusable original waveforms. Never owns originals.</summary>
public sealed class PreviewCache
{
    public const double WindowSeconds = 20;
    public const long DefaultLimitBytes = 512L * 1024 * 1024;
    private readonly string _root;
    private readonly long _limit;
    private readonly FfmpegTools _tools;
    private readonly SemaphoreSlim _previewGate = new(1, 1), _waveformGate = new(1, 1);
    private readonly object _files = new();
    private readonly Dictionary<string, int> _pins = new(StringComparer.OrdinalIgnoreCase);

    public PreviewCache(FfmpegTools tools, string directory, long limitBytes = DefaultLimitBytes)
    {
        if (limitBytes < 1) throw new ArgumentOutOfRangeException(nameof(limitBytes));
        _tools = tools; _root = Path.GetFullPath(directory); _limit = limitBytes;
        CheckDirectory();
    }
    private void CheckDirectory()
    {
        // No recursive deletion, directory traversal or reparse-point cleanup.
        for (var node = new DirectoryInfo(_root); node != null; node = node.Parent)
            if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("미리보기 캐시 폴더에 연결 경로를 사용할 수 없습니다.");
        Directory.CreateDirectory(_root);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string SourceKey(RecordingManifest recording)
    {
        var file = new FileInfo(MediaPaths.ExistingFile(recording.MediaPath));
        return Hash(JsonSerializer.Serialize(new { Version = 1, file.FullName, file.Length,
            Modified = file.LastWriteTimeUtc.Ticks, Created = file.CreationTimeUtc.Ticks,
            recording.DurationSeconds, recording.HasVideo, recording.Tracks }));
    }
    private string CachePath(string key, string extension) => Path.Combine(_root, "vv-" + key + extension);

    public async Task<PreviewSlice> GetSliceAsync(RecordingManifest recording, IReadOnlyList<ExportTrack> tracks,
        double position, CancellationToken token = default)
    {
        if (!double.IsFinite(position) || !double.IsFinite(recording.DurationSeconds) || recording.DurationSeconds <= 0)
            throw new ArgumentException("Invalid preview position or source duration.");
        var clamped = Math.Clamp(position, 0, Math.Max(0, recording.DurationSeconds - 0.001));
        var start = Math.Floor(clamped / WindowSeconds) * WindowSeconds;
        var end = Math.Min(recording.DurationSeconds, start + WindowSeconds);
        var mix = tracks.OrderBy(t => t.Index).ToArray();
        await _previewGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckDirectory(); token.ThrowIfCancellationRequested();
            var key = Hash(SourceKey(recording) + JsonSerializer.Serialize(new { start, end, mix }));
            var path = CachePath(key, recording.HasVideo ? ".mp4" : ".m4a");
            var hit = false;
            lock (_files) _pins[path] = _pins.GetValueOrDefault(path) + 1;
            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var info = await _tools.ProbeAsync(path, token).ConfigureAwait(false);
                        hit = info.HasVideo == recording.HasVideo && Math.Abs(info.DurationSeconds - (end - start)) < 0.15;
                    }
                    catch (Exception e) when (e is IOException or InvalidOperationException or JsonException) { }
                    if (!hit) File.Delete(path);
                }
                if (!hit) await new PreviewService(_tools).CreateRangeAsync(recording, mix, path, start, end - start, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                Prune();
                return new PreviewSlice(path, start, end, hit, () => Release(path));
            }
            catch { Release(path); throw; }
        }
        finally { _previewGate.Release(); }
    }

    public async Task<IReadOnlyList<AudioWaveform>> GetWaveformsAsync(RecordingManifest recording, CancellationToken token = default)
    {
        await _waveformGate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            CheckDirectory(); token.ThrowIfCancellationRequested();
            var path = CachePath(SourceKey(recording), ".wave.json");
            if (File.Exists(path) && new FileInfo(path).Length <= 2 * 1024 * 1024)
            {
                try
                {
                    var data = JsonSerializer.Deserialize<AudioWaveform[]>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                    if (data != null && data.Length == recording.Tracks.Count && data.Zip(recording.Tracks).All(pair =>
                        pair.First != null && pair.First.Name == pair.Second.Name && pair.First.Peaks is { Length: 1600 } &&
                        pair.First.Peaks.All(p => float.IsFinite(p) && p >= 0 && p <= 1)))
                    { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return data; }
                }
                catch (Exception e) when (e is IOException or JsonException) { }
            }
            var result = await new WaveformService(_tools).ReadAsync(recording, token: token).ConfigureAwait(false);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_files) { File.Move(temporary, path, true); Prune(); }
            return result;
        }
        finally
        {
            if (temporary != null) await MediaPaths.DeleteTemporaryAsync(temporary).ConfigureAwait(false);
            _waveformGate.Release();
        }
    }

    private void Release(string path)
    {
        lock (_files)
        {
            if (_pins.TryGetValue(path, out var count))
            { if (count == 1) _pins.Remove(path); else _pins[path] = count - 1; }
            Prune();
        }
    }
    private void Prune()
    {
        lock (_files)
        {
            try
            {
                CheckDirectory();
                foreach (var temporary in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                    .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1) &&
                        System.Text.RegularExpressions.Regex.IsMatch(f.Name, @"^(\.vv-[0-9A-F]{64}\.(mp4|m4a)\.[0-9a-f]{32}\.partial|vv-[0-9A-F]{64}\.wave\.json\.[0-9a-f]{32}\.tmp)$")))
                { try { temporary.Delete(); } catch (IOException) { } }
                var files = new DirectoryInfo(_root).EnumerateFiles("vv-*", SearchOption.TopDirectoryOnly)
                    .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        System.Text.RegularExpressions.Regex.IsMatch(f.Name, @"^vv-[0-9A-F]{64}\.(mp4|m4a|wave\.json)$"))
                    .OrderBy(f => f.LastWriteTimeUtc).ToArray();
                var bytes = files.Sum(f => f.Length);
                foreach (var file in files)
                {
                    if (bytes <= _limit) break;
                    if (_pins.ContainsKey(file.FullName)) continue;
                    var length = file.Length;
                    try { file.Delete(); bytes -= length; } catch (IOException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
