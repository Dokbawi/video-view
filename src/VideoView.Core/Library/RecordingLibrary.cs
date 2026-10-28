using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace VideoView.Core.Library;

/// <summary>
/// Owns recording manifests and their original media under root/yyyy-MM-dd.
/// Export files are not indexed, renamed, or deleted. Display-title renames do not move media.
/// </summary>
public sealed class RecordingLibrary
{
    private const string ManifestSuffix = ".recording.json";
    private const long MaximumManifestBytes = 1024 * 1024;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate;
    private IReadOnlyList<string> _warnings = [];

    public RecordingLibrary(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("The library directory must be an absolute path.", nameof(outputDirectory));
        RootDirectory = Path.TrimEndingDirectorySeparator(FullPath(outputDirectory));
        RejectLinks(RootDirectory);
        _gate = Gates.GetOrAdd(RootDirectory, _ => new SemaphoreSlim(1, 1));
    }

    public string RootDirectory { get; }
    public string OutputDirectory => RootDirectory;

    /// <summary>Unreadable or invalid manifests skipped by the most recent List call.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Lists only validated original manifests, newest first. Does not scan media or export files.</summary>
    public IReadOnlyList<RecordingManifest> List()
    {
        _gate.Wait();
        try
        {
            var warnings = new List<string>();
            var entries = ReadEntries(warnings);
            _warnings = warnings.AsReadOnly();
            return entries.Select(entry => entry.Manifest).OrderByDescending(item => item.CreatedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<RecordingManifest>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(List());
    }

    /// <summary>Returns a unique media destination; the caller creates the media before AddAsync.</summary>
    public string CreateRecordingPath(DateTimeOffset createdAt, string extension)
    {
        extension = ValidateExtension(extension);
        _gate.Wait();
        try
        {
            var directory = DateDirectory(createdAt);
            EnsureDirectory(directory);
            return CheckedPath(Path.Combine(directory, $"{createdAt:HHmmssfff}-{Guid.NewGuid():N}{extension}"));
        }
        finally { _gate.Release(); }
    }

    public Task<RecordingManifest> AddAsync(RecordingManifest recording, CancellationToken cancellationToken = default) =>
        WriteAsync(recording, addOnly: true, cancellationToken);

    /// <summary>Adds a new manifest or updates existing metadata. An existing original cannot be retargeted.</summary>
    public Task<RecordingManifest> SaveAsync(RecordingManifest recording, CancellationToken cancellationToken = default) =>
        WriteAsync(recording, addOnly: false, cancellationToken);

    public RecordingManifest Save(RecordingManifest recording) => SaveAsync(recording).GetAwaiter().GetResult();

    /// <summary>Finds an existing original manifest by its validated identifier.</summary>
    public string GetManifestPath(string id)
    {
        ValidateId(id);
        _gate.Wait();
        try { return FindEntry(id).Path; }
        finally { _gate.Release(); }
    }

    /// <summary>Changes only the display title. File names and independent audio tracks stay intact.</summary>
    public RecordingManifest Rename(string id, string title)
    {
        ValidateId(id);
        ValidateTitle(title);
        _gate.Wait();
        try
        {
            var entry = FindEntry(id);
            entry.Manifest.Title = title.Trim();
            WriteManifestAsync(entry.Manifest, entry.Path, overwrite: true, CancellationToken.None).GetAwaiter().GetResult();
            return entry.Manifest;
        }
        finally { _gate.Release(); }
    }

    public RecordingManifest Rename(RecordingManifest recording, string title)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return Rename(recording.Id, title);
    }

    public void SaveEdits(string id, RecordingEdits edits) => UpdateMetadata(id,
        manifest => manifest.Edits = edits.Normalize(manifest));

    public void SetFavorite(string id, bool favorite) => UpdateMetadata(id,
        manifest => manifest.IsFavorite = favorite);

    private void UpdateMetadata(string id, Action<RecordingManifest> update)
    {
        ValidateId(id);
        _gate.Wait();
        try
        {
            // Read the current manifest so editing cannot revert a rename or favorite change.
            var entry = FindEntry(id);
            update(entry.Manifest);
            WriteManifestAsync(entry.Manifest, entry.Path, true, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Permanently removes only the original media referenced by a stored manifest and that manifest.</summary>
    public void Delete(string id)
    {
        ValidateId(id);
        _gate.Wait();
        try
        {
            var entry = FindEntry(id);
            var media = CheckedPath(entry.Manifest.MediaPath);
            // Never trust a path supplied by the caller, or remove a directory recursively.
            if (ReadEntries([]).Any(other => other.Manifest.Id != id && PathComparer.Equals(other.Manifest.MediaPath, media)))
                throw new InvalidOperationException("This original is referenced by another recording and cannot be deleted.");
            CheckedPath(entry.Path);
            File.Delete(media);
            File.Delete(entry.Path);
        }
        finally { _gate.Release(); }
    }

    public void Delete(RecordingManifest recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        Delete(recording.Id);
    }

    private async Task<RecordingManifest> WriteAsync(RecordingManifest recording, bool addOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        // Snapshot mutable UI models before awaiting the library lock.
        var snapshot = Snapshot(recording);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateManifest(snapshot, requireMedia: true);
            var entries = ReadEntries([]);
            var matches = entries.Where(entry => entry.Manifest.Id == snapshot.Id).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("Duplicate recording identifiers were found.");
            var existing = matches.SingleOrDefault();
            if (addOnly && existing is not null) throw new IOException("This recording identifier already exists.");
            if (existing is not null && (!PathComparer.Equals(existing.Manifest.MediaPath, snapshot.MediaPath) ||
                                         existing.Manifest.CreatedAt != snapshot.CreatedAt))
                throw new ArgumentException("An existing recording's original path and creation date cannot be changed.", nameof(recording));
            if (entries.Any(entry => entry.Manifest.Id != snapshot.Id && PathComparer.Equals(entry.Manifest.MediaPath, snapshot.MediaPath)))
                throw new ArgumentException("This original media file already belongs to a recording.", nameof(recording));
            var path = existing?.Path ?? CheckedPath(Path.Combine(DateDirectory(snapshot.CreatedAt), snapshot.Id + ManifestSuffix));
            EnsureDirectory(Path.GetDirectoryName(path)!);
            await WriteManifestAsync(snapshot, path, existing is not null, cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    private async Task WriteManifestAsync(RecordingManifest manifest, string path, bool overwrite,
        CancellationToken cancellationToken)
    {
        CheckedPath(path);
        var temporary = CheckedPath(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            CheckedPath(path);
            CheckedPath(temporary);
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            try { File.Delete(CheckedPath(temporary)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    private Entry FindEntry(string id)
    {
        var matches = ReadEntries([]).Where(entry => entry.Manifest.Id == id).ToArray();
        return matches.Length switch
        {
            0 => throw new FileNotFoundException($"Recording '{id}' was not found in this library."),
            1 => matches[0],
            _ => throw new InvalidDataException("Duplicate recording identifiers were found.")
        };
    }

    private List<Entry> ReadEntries(List<string> warnings)
    {
        var entries = new List<Entry>();
        RejectLinks(RootDirectory);
        if (!Directory.Exists(RootDirectory)) return entries;
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(directory), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) continue;
            try
            {
                CheckedPath(directory);
                foreach (var file in Directory.EnumerateFiles(directory, "*" + ManifestSuffix, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        CheckedPath(file);
                        if (new FileInfo(file).Length > MaximumManifestBytes)
                            throw new InvalidDataException("Manifest exceeds the maximum supported size.");
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var manifest = JsonSerializer.Deserialize<RecordingManifest>(stream, JsonOptions)
                            ?? throw new InvalidDataException("Manifest is empty.");
                        ValidateManifest(manifest, requireMedia: false);
                        var expected = Path.Combine(DateDirectory(manifest.CreatedAt), manifest.Id + ManifestSuffix);
                        var adjacentSidecar = manifest.MediaPath + ManifestSuffix;
                        if (!PathComparer.Equals(file, expected) && !PathComparer.Equals(file, adjacentSidecar))
                            throw new InvalidDataException("Manifest filename or date does not match its contents.");
                        entries.Add(new Entry(file, manifest));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
                    {
                        warnings.Add($"{Path.GetFileName(file)}: {e.Message}");
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                warnings.Add($"{Path.GetFileName(directory)}: {e.Message}");
            }
        }
        return entries;
    }

    private void ValidateManifest(RecordingManifest manifest, bool requireMedia)
    {
        if (manifest.Version != 1) throw new ArgumentException("Unsupported recording manifest version.");
        ValidateId(manifest.Id);
        ValidateTitle(manifest.Title);
        if (!double.IsFinite(manifest.DurationSeconds) || manifest.DurationSeconds < 0)
            throw new ArgumentException("Recording duration must be finite and nonnegative.");
        if (manifest.Tracks is null || manifest.Tracks.Any(track => track is null || track.Index < 0 || track.Name is null) ||
            manifest.Tracks.Select(track => track.Index).Distinct().Count() != manifest.Tracks.Count)
            throw new ArgumentException("Recording audio tracks require unique nonnegative indices and names.");
        manifest.MediaPath = CheckedPath(manifest.MediaPath);
        ValidateExtension(Path.GetExtension(manifest.MediaPath));
        if (!PathComparer.Equals(Path.GetDirectoryName(manifest.MediaPath), DateDirectory(manifest.CreatedAt)))
            throw new ArgumentException("Original media must be directly inside its recording date folder.");
        if (Directory.Exists(manifest.MediaPath)) throw new ArgumentException("Original media must be a file.");
        if (requireMedia && !File.Exists(manifest.MediaPath))
            throw new FileNotFoundException("Original recording media was not found.", manifest.MediaPath);
    }

    private string DateDirectory(DateTimeOffset createdAt) =>
        CheckedPath(Path.Combine(RootDirectory, createdAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    private string CheckedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Library file paths must be absolute.", nameof(path));
        var full = FullPath(path);
        var relative = Path.GetRelativePath(RootDirectory, full);
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The path is outside this recording library.", nameof(path));
        RejectLinks(full);
        return full;
    }

    private static string FullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Any(char.IsControl)) throw new ArgumentException("Library paths cannot contain control characters.", nameof(path));
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':'))
            throw new ArgumentException("Alternate data stream paths are not supported.", nameof(path));
        return full;
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Recording library paths cannot contain symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void EnsureDirectory(string directory)
    {
        CheckedPath(directory);
        Directory.CreateDirectory(directory);
        CheckedPath(directory);
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Recording identifiers may contain only ASCII letters, digits, '-' and '_'.", nameof(id));
    }

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 256 || title.Any(char.IsControl))
            throw new ArgumentException("A recording title must contain 1 to 256 printable characters.", nameof(title));
    }

    private static string ValidateExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        extension = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        if (extension is not (".mp4" or ".m4a" or ".mkv" or ".mka" or ".wav" or ".mov"))
            throw new ArgumentException("Unsupported original media extension.", nameof(extension));
        return extension;
    }

    private static RecordingManifest Snapshot(RecordingManifest recording) => new()
    {
        Version = recording.Version,
        Id = recording.Id,
        Title = recording.Title,
        CreatedAt = recording.CreatedAt,
        MediaPath = recording.MediaPath,
        DurationSeconds = recording.DurationSeconds,
        HasVideo = recording.HasVideo,
        Tracks = recording.Tracks?.ToList()!,
        Recovered = recording.Recovered,
        IsFavorite = recording.IsFavorite,
        Edits = recording.Edits?.Normalize(recording)
    };

    private sealed record Entry(string Path, RecordingManifest Manifest);
}
