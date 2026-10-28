using System.Globalization;
using System.Text;

namespace VideoView.Core;

/// <summary>Reads an append-only FFmpeg CSV once. Holds only an incomplete line between
/// polls; never rewrites the recovery log. A truncated/malformed log fails closed.</summary>
public sealed class SegmentCsvTail
{
    private readonly byte[] _readBuffer = new byte[16 * 1024];
    private readonly List<byte> _line = [];
    private long _offset;
    public long BytesRead { get; private set; }
    public long RowsRead { get; private set; }
    public int PendingBytes => _line.Count;

    // Caller serializes calls. Returning bounded batches also bounds catch-up allocation.
    public async Task<IReadOnlyList<SegmentInfo>> ReadAsync(string directory, CancellationToken token = default)
    {
        var csv = Path.Combine(directory, "segments.csv");
        if (!File.Exists(csv))
        {
            if (_offset > 0) throw new IOException("녹화 조각 목록이 사라졌습니다. 복구 자료를 보존합니다.");
            return [];
        }
        using var stream = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            _readBuffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < _offset) throw new InvalidDataException("녹화 조각 목록이 잘렸습니다. 복구 자료를 보존합니다.");
        stream.Position = _offset;
        var result = new List<SegmentInfo>();
        var remaining = Math.Min(stream.Length - _offset, 64 * 1024);
        while (remaining > 0)
        {
            var count = await stream.ReadAsync(_readBuffer.AsMemory(0, (int)Math.Min(remaining, _readBuffer.Length)), token);
            if (count == 0) break;
            _offset += count;
            BytesRead += count;
            remaining -= count;
            for (var i = 0; i < count; i++)
            {
                if (_readBuffer[i] != (byte)'\n')
                {
                    if (_line.Count >= 16 * 1024) throw new InvalidDataException("녹화 조각 목록의 한 줄이 너무 깁니다.");
                    _line.Add(_readBuffer[i]);
                    continue;
                }
                var line = Encoding.UTF8.GetString(_line.ToArray()).Trim().TrimStart('\uFEFF');
                _line.Clear();
                if (line.Length == 0) continue;
                result.Add(Parse(directory, line));
                RowsRead++;
            }
        }
        return result;
    }

    private static SegmentInfo Parse(string directory, string line)
    {
        var last = line.LastIndexOf(',');
        var previous = last > 0 ? line.LastIndexOf(',', last - 1) : -1;
        if (previous < 0) throw new InvalidDataException("녹화 조각 목록 형식 오류.");
        var name = line[..previous].Trim('"');
        // FFmpeg writes the segment basename. Reject paths and unexpected files entirely.
        if (name.Length != 17 || !name.StartsWith("part_", StringComparison.Ordinal) ||
            !name.EndsWith(".mkv", StringComparison.Ordinal) || name.AsSpan(5, 8).ContainsAnyExceptInRange('0', '9') ||
            !double.TryParse(line[(previous + 1)..last], CultureInfo.InvariantCulture, out var start) ||
            !double.TryParse(line[(last + 1)..], CultureInfo.InvariantCulture, out var end) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            throw new InvalidDataException("녹화 조각 목록의 이름 또는 시간이 올바르지 않습니다.");
        var path = Path.Combine(directory, name);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("닫힌 녹화 조각을 안전하게 읽을 수 없습니다: " + name);
        return new(path, start, end, info.Length);
    }
}
