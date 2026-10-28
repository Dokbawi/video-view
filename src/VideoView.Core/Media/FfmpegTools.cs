using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VideoView.Core.Media;

/// <summary>Runs local FFmpeg tools without a shell. Audio indices are zero-based audio ordinals.</summary>
public sealed class FfmpegTools
{
    private const int DiagnosticLimit = 64 * 1024;
    private const int OutputLimit = 1024 * 1024;

    public FfmpegTools(string ffmpegPath, string ffprobePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
    }

    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    /// <summary>Explicit setting, application-adjacent tools, then PATH. Missing tools fail at CheckAsync.</summary>
    public static FfmpegTools Resolve(string? ffmpegPath = null, string? ffprobePath = null) =>
        new(ResolveExecutable(ffmpegPath, "ffmpeg"), ResolveExecutable(ffprobePath, "ffprobe"));

    /// <summary>Throws an actionable exception if either executable cannot run.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        await RunProcessAsync(FfmpegPath, ["-version"], null, cancellationToken).ConfigureAwait(false);
        await RunProcessAsync(FfprobePath, ["-version"], null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns bounded stdout; nonzero exits throw FfmpegException with bounded stderr.</summary>
    public async Task<string> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken = default) =>
        (await RunProcessAsync(FfmpegPath, args, null, cancellationToken).ConfigureAwait(false)).Output;

    public Task<string> ConcatenateAsync(IReadOnlyList<string> segmentPaths, string outputPath,
        bool hasVideo, CancellationToken cancellationToken = default) =>
        ArchiveMedia.ConcatenateAsync(this, segmentPaths, outputPath, hasVideo, cancellationToken);

    /// <summary>Joins recorder segments using the capture timeline, excluding accumulated codec padding.</summary>
    public Task<string> ConcatenateSegmentsAsync(IReadOnlyList<SegmentInfo> segments, string outputPath,
        bool hasVideo, CancellationToken cancellationToken = default) =>
        ArchiveMedia.ConcatenateAsync(this, segments.Select(segment => segment.Path).ToArray(), outputPath,
            hasVideo, cancellationToken, segments.Select(segment => segment.Duration).ToArray());

    public Task<string> TrimArchiveAsync(string inputPath, string outputPath, double startSeconds,
        double durationSeconds, bool hasVideo, CancellationToken cancellationToken = default) =>
        ArchiveMedia.TrimAsync(this, inputPath, outputPath, startSeconds, durationSeconds, hasVideo, cancellationToken);

    internal async Task RunWithProgressAsync(IEnumerable<string> args, Action<string> onLine,
        CancellationToken cancellationToken) =>
        _ = await RunProcessAsync(FfmpegPath, args, onLine, cancellationToken).ConfigureAwait(false);

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        path = MediaPaths.ExistingFile(path);
        var result = await RunProcessAsync(FfprobePath,
            ["-v", "error", "-show_entries",
                "format=duration:stream=index,codec_type,duration,width,height,avg_frame_rate:stream_tags=title,handler_name:stream_disposition=attached_pic",
                "-of", "json", "-i", path], null, cancellationToken).ConfigureAwait(false);
        if (result.Truncated)
            throw new InvalidDataException("FFprobe output exceeded the supported metadata size.");

        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        var duration = root.TryGetProperty("format", out var format) ? ReadDuration(format) : 0;
        var hasVideo = false;
        int width = 0, height = 0; double frameRate = 0;
        var tracks = new List<TrackInfo>();
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                duration = Math.Max(duration, ReadDuration(stream));
                var type = stream.TryGetProperty("codec_type", out var codecType) ? codecType.GetString() : null;
                if (type == "video")
                {
                    var attachedPicture = stream.TryGetProperty("disposition", out var disposition) &&
                        disposition.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() != 0;
                    if (!attachedPicture && !hasVideo)
                    {
                        width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        if (stream.TryGetProperty("avg_frame_rate", out var rate))
                        {
                            var parts = (rate.GetString() ?? "").Split('/');
                            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0 &&
                                double.IsFinite(numerator) && numerator > 0) frameRate = numerator / denominator;
                        }
                    }
                    hasVideo |= !attachedPicture;
                }
                if (type != "audio") continue;
                var name = $"Audio {tracks.Count + 1}";
                if (stream.TryGetProperty("tags", out var tags))
                {
                    if (tags.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()))
                        name = title.GetString()!;
                    else if (tags.TryGetProperty("handler_name", out var handler) &&
                             !string.IsNullOrWhiteSpace(handler.GetString()))
                        name = handler.GetString()!;
                }
                tracks.Add(new TrackInfo(tracks.Count, name));
            }
        }
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("The source has no finite, positive media duration.");
        return new MediaInfo(duration, hasVideo, tracks, width, height, frameRate);
    }

    private static double ReadDuration(JsonElement element)
    {
        if (!element.TryGetProperty("duration", out var value)) return 0;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
               double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
    }

    private static string ResolveExecutable(string? setting, string name)
    {
        if (!string.IsNullOrWhiteSpace(setting)) return Environment.ExpandEnvironmentVariables(setting.Trim());
        var executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var adjacent = Path.Combine(AppContext.BaseDirectory, "tools", executable);
        if (File.Exists(adjacent)) return adjacent;
        // Also supports tools placed directly beside a published application.
        adjacent = Path.Combine(AppContext.BaseDirectory, executable);
        if (File.Exists(adjacent)) return adjacent;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var repositoryTool = Path.Combine(directory.FullName, ".tools", "ffmpeg", "bin", executable);
                if (File.Exists(repositoryTool)) return repositoryTool;
            }
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(
                    Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), executable));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { }
        }
        return executable;
    }

    private static async Task<ProcessOutput> RunProcessAsync(string executable, IEnumerable<string> args,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in args)
        {
            ArgumentNullException.ThrowIfNull(argument);
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new IOException($"Could not start {executable}.");
        }
        catch (Win32Exception e)
        {
            throw new IOException($"Could not start '{executable}'. Install FFmpeg or set its executable paths.", e);
        }

        using var registration = cancellationToken.Register(() => Kill(process));
        async Task<CapturedText> CaptureAsync(StreamReader reader, int limit, Action<string>? callback)
        {
            try { return await ReadBoundedAsync(reader, limit, callback, cancellationToken).ConfigureAwait(false); }
            catch
            {
                // A failed reader or progress callback must not leave the child blocked on a full pipe.
                Kill(process);
                throw;
            }
        }
        var stdout = CaptureAsync(process.StandardOutput, OutputLimit, onLine);
        var stderr = CaptureAsync(process.StandardError, DiagnosticLimit, null);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new FfmpegException(executable, process.ExitCode, error.Text);
            return new ProcessOutput(output.Text, output.Truncated);
        }
        catch
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or Win32Exception) { }
            // Observe pipe failures as well as process failures.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private static async Task<CapturedText> ReadBoundedAsync(StreamReader reader, int limit,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var captured = new StringBuilder();
        var line = onLine is null ? null : new StringBuilder();
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            captured.Append(buffer, 0, read);
            if (captured.Length > limit)
            {
                captured.Remove(0, captured.Length - limit);
                truncated = true;
            }
            if (line is null) continue;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    onLine!(line.ToString().TrimEnd('\r'));
                    line.Clear();
                }
                else if (line.Length < 4096) line.Append(buffer[i]);
            }
        }
        if (line is { Length: > 0 }) onLine!(line.ToString());
        return new CapturedText(captured.ToString(), truncated);
    }

    private sealed record CapturedText(string Text, bool Truncated);
    private sealed record ProcessOutput(string Output, bool Truncated);
}

public sealed class FfmpegException(string executablePath, int exitCode, string diagnostics)
    : IOException($"{Path.GetFileName(executablePath)} exited with code {exitCode}. {diagnostics}")
{
    public string ExecutablePath { get; } = executablePath;
    public int ExitCode { get; } = exitCode;
    public string Diagnostics { get; } = diagnostics;
}
