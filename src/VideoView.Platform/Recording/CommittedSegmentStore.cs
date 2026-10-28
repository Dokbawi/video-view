using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VideoView.Platform.Recording;

/// <summary>Promotes immutable, closed encoder segments. Same-volume hard links avoid
/// duplicate writes; other volumes/filesystems use an atomic, cancellable stream copy.
/// Neither source contents nor existing destination contents are ever overwritten.</summary>
public sealed class CommittedSegmentStore
{
    private long _hardLinks, _copies, _copiedBytes;
    public long HardLinkCount => Interlocked.Read(ref _hardLinks);
    public long CopyCount => Interlocked.Read(ref _copies);
    public long CopiedBytes => Interlocked.Read(ref _copiedBytes);

    public async Task PromoteAsync(string source, string destination, CancellationToken token = default,
        bool allowHardLink = true)
    {
        token.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("원본과 보존 경로가 같습니다.");
        RejectReparsePath(source);
        RejectReparsePath(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            RejectReparsePath(destination);
            if (new FileInfo(source).Length != new FileInfo(destination).Length ||
                !await IdenticalAsync(source, destination, token))
                throw new IOException("보존된 녹화 조각이 원본과 다릅니다. 두 파일을 그대로 보존합니다.");
            return;
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".copying";
        var linked = false;
        var bytes = new FileInfo(source).Length;
        try
        {
            if (allowHardLink && OperatingSystem.IsWindows())
            {
                linked = CreateHardLink(temporary, source, IntPtr.Zero);
                if (!linked)
                {
                    var error = Marshal.GetLastWin32Error();
                    // Cross-volume, unsupported filesystem, link limit or privilege restrictions.
                    if (error is not (1 or 5 or 17 or 50 or 1142 or 1314))
                        throw new IOException("녹화 조각 보존 실패: " + new Win32Exception(error).Message);
                }
            }
            if (!linked)
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 128 * 1024, token);
                await output.FlushAsync(token);
                if (input.Length != bytes || output.Length != bytes)
                    throw new IOException("닫힌 녹화 조각의 크기가 복사 도중 바뀌었습니다.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
            if (linked) Interlocked.Increment(ref _hardLinks);
            else { Interlocked.Increment(ref _copies); Interlocked.Add(ref _copiedBytes, bytes); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<bool> IdenticalAsync(string left, string right, CancellationToken token)
    {
        await using var a = File.OpenRead(left);
        await using var b = File.OpenRead(right);
        var leftHash = await SHA256.HashDataAsync(a, token);
        var rightHash = await SHA256.HashDataAsync(b, token);
        return leftHash.SequenceEqual(rightHash);
    }

    private static void RejectReparsePath(string path)
    {
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("녹화 조각 보존 경로에 링크 또는 연결 지점이 있습니다.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
