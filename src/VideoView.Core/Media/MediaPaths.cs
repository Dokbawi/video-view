namespace VideoView.Core.Media;

internal static class MediaPaths
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string FullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Any(char.IsControl)) throw new ArgumentException("Media paths cannot contain control characters.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && fullPath.AsSpan(Path.GetPathRoot(fullPath)!.Length).Contains(':'))
            throw new ArgumentException("Alternate data stream paths are not supported.", nameof(path));
        return fullPath;
    }

    internal static string ExistingFile(string path)
    {
        path = FullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Media file was not found.", path);
        return path;
    }

    internal static string NewOutput(string outputPath, IEnumerable<string> inputs)
    {
        var output = FullPath(outputPath);
        if (inputs.Any(input => string.Equals(FullPath(input), output, Comparison)))
            throw new ArgumentException("Output must not replace an original media file.", nameof(outputPath));
        if (File.Exists(output) || Directory.Exists(output))
            throw new IOException($"Output already exists: {output}");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        return output;
    }

    internal static string TemporarySibling(string output, string suffix) =>
        Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}{suffix}");

    internal static async Task DeleteTemporaryAsync(string path)
    {
        // Windows can briefly retain encoder file handles after process-exit notification.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) return;
                await Task.Delay(40 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }
}
