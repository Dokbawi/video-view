using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace VideoView.Core.Media;

public sealed record AudioWaveform(string Name, float[] Peaks);

/// <summary>Streams decoded samples into bounded peak bins; never loads full recordings into memory.</summary>
public sealed class WaveformService(FfmpegTools tools)
{
    public async Task<IReadOnlyList<AudioWaveform>> ReadAsync(RecordingManifest recording, int bins = 1600,
        CancellationToken token = default)
    {
        if (bins is < 16 or > 10000 || !double.IsFinite(recording.DurationSeconds) || recording.DurationSeconds <= 0)
            throw new ArgumentException("Invalid waveform size or duration.");
        var path = MediaPaths.ExistingFile(recording.MediaPath);
        var waveforms = new List<AudioWaveform>();
        foreach (var track in recording.Tracks)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(tools.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", $"0:a:{track.Index}",
                "-t", recording.DurationSeconds.ToString("G17", CultureInfo.InvariantCulture), "-af", "aresample=8000:async=1:first_pts=0",
                "-ac", "1", "-ar", "8000", "-f", "f32le", "pipe:1" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("파형 분석을 시작할 수 없습니다.");
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var errors = process.StandardError.ReadToEndAsync();
            var buffer = new byte[65536 + 4];
            var peaks = new float[bins];
            var carry = 0; long sample = 0;
            try
            {
                while (true)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(carry, 65536), token).ConfigureAwait(false);
                    if (read == 0) break;
                    var available = carry + read;
                    var complete = available - available % 4;
                    for (var offset = 0; offset < complete; offset += 4, sample++)
                    {
                        var value = Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset, 4)));
                        var bin = Math.Min(bins - 1, (int)(sample / (recording.DurationSeconds * 8000) * bins));
                        if (float.IsFinite(value)) peaks[bin] = Math.Max(peaks[bin], Math.Min(1, value));
                    }
                    carry = available - complete;
                    if (carry > 0) Buffer.BlockCopy(buffer, complete, buffer, 0, carry);
                }
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var error = await errors.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException("파형 분석 실패: " + error[..Math.Min(error.Length, 1000)]);
                waveforms.Add(new(track.Name, peaks));
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } }
        }
        return waveforms;
    }
}
