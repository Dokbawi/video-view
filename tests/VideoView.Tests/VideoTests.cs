using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VideoView.Platform.Video;

namespace VideoView.Tests;

public static class VideoTests
{
    /// <summary>Captures, resizes, restarts, and closes a dedicated test window owned by this process.</summary>
    /// <remarks>
    /// The caller must supply a visible, non-minimized disposable test window and keep its UI
    /// message loop running while awaiting this method. The supplied window is closed by the test.
    /// Requires a desktop-capable token; Windows denies WGC under restricted sandbox tokens.
    /// Does not capture any monitor or unrelated window, and does not write pixel files.
    /// </remarks>
    public static async Task NativeSmokeAsync(nint ownWindowHandle, Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        void Check(bool condition, string message)
        {
            assert(condition, message);
            if (!condition) throw new InvalidOperationException(message);
        }

        GetWindowThreadProcessId(ownWindowHandle, out uint process);
        Check(ownWindowHandle != 0 && IsWindow(ownWindowHandle) && process == Environment.ProcessId,
            "WGC test target belongs to this process");
        Check(!CaptureTargetCatalog.List().Any(t => t.Kind == CaptureTargetKind.Window && t.Handle == ownWindowHandle),
            "Catalog excludes own-process windows");

        using var source = new ScreenCaptureSource(new CaptureTarget("native-smoke", "WGC own test window", CaptureTargetKind.Window, ownWindowHandle));
        var faultSignal = NewSignal();
        int faultCount = 0;
        source.Faulted += message =>
        {
            Interlocked.Increment(ref faultCount);
            source.Stop(); // Verifies that a fault subscriber can join capture shutdown safely.
            Volatile.Read(ref faultSignal).TrySetResult(message);
        };

        source.Start();
        source.Start();
        Check(source.BorderlessAccessGranted && string.IsNullOrEmpty(source.CaptureNotice),
            "Windows grants borderless access and the native session disables its capture border");
        var first = await WaitForFrameAsync(source, faultSignal.Task).ConfigureAwait(false);
        Check(first.Width > 0 && first.Height > 0 && first.Sequence > 0 &&
            first.Pixels.Length == checked(first.Width * first.Height * 4), "WGC publishes a complete tight BGRA frame");
        var retainedBytes = first.Pixels.ToArray();

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                var frame = source.LatestFrame;
                if (frame is null || frame.Width != first.Width || frame.Height != first.Height ||
                    frame.Pixels.Length != checked(frame.Width * frame.Height * 4)) return false;
            }
            return true;
        })).ToArray();
        Check((await Task.WhenAll(readers).ConfigureAwait(false)).All(valid => valid), "Concurrent readers observe complete fixed-size snapshots");
        source.Stop();
        source.Stop();
        Check(source.LatestFrame is null && first.Pixels.SequenceEqual(retainedBytes), "Stop clears latest; retained frame remains valid");

        source.Start();
        var restarted = await WaitForFrameAsync(source, faultSignal.Task).ConfigureAwait(false);
        Check(restarted.Sequence > first.Sequence && !ReferenceEquals(first.Pixels, restarted.Pixels) &&
            first.Pixels.SequenceEqual(retainedBytes), "Restart advances sequence without reusing published arrays");

        Check(GetWindowRect(ownWindowHandle, out var bounds), "Read test-window size");
        Check(SetWindowPos(ownWindowHandle, 0, 0, 0, bounds.Right - bounds.Left + 64,
            bounds.Bottom - bounds.Top + 48, 0x0016 /* NOMOVE | NOZORDER | NOACTIVATE */), "Resize only the owned test window");
        var resized = await WaitForFrameAsync(source, faultSignal.Task,
            frame => frame.Width != first.Width || frame.Height != first.Height).ConfigureAwait(false);
        Check(faultCount == 0 && resized.Sequence > restarted.Sequence,
            "Growing the capture window recreates its pool and continues without fault");
        Check(SetWindowPos(ownWindowHandle, 0, 0, 0, bounds.Right - bounds.Left - 80,
            bounds.Bottom - bounds.Top - 60, 0x0016), "Shrink only the owned test window");
        var smaller = await WaitForFrameAsync(source, faultSignal.Task,
            frame => frame.Width < first.Width && frame.Height < first.Height).ConfigureAwait(false);
        Check(smaller.Pixels.Length == smaller.Width * smaller.Height * 4 && faultCount == 0 &&
            first.Pixels.SequenceEqual(retainedBytes), "Shrinking retains complete frames and previous immutable snapshots");
        Check(SetWindowPos(ownWindowHandle, 0, 0, 0, bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top, 0x0016), "Restore the owned test window size");
        await WaitForFrameAsync(source, faultSignal.Task,
            frame => frame.Width == first.Width && frame.Height == first.Height).ConfigureAwait(false);
        Check(PostMessage(ownWindowHandle, 0x0010 /* WM_CLOSE */, 0, 0), "Close only the owned test window");
        string closeFault = await faultSignal.Task.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        Check(closeFault.Contains("closed", StringComparison.OrdinalIgnoreCase) && source.LatestFrame is null && faultCount == 1,
            "Closed capture item explicitly faults once; fault handler can Stop");

        using var invalid = new ScreenCaptureSource(new CaptureTarget("invalid", "Invalid test HWND", CaptureTargetKind.Window, -1));
        bool invalidRejected = false;
        try { invalid.Start(); } catch (InvalidOperationException) { invalidRejected = true; }
        Check(invalidRejected && invalid.LatestFrame is null, "Invalid HWND fails without monitor fallback");
        source.Dispose();
        bool disposedRejected = false;
        try { source.Start(); } catch (ObjectDisposedException) { disposedRejected = true; }
        Check(disposedRejected, "Disposed capture cannot restart");
    }

    private static TaskCompletionSource<string> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<VideoFrame> WaitForFrameAsync(ScreenCaptureSource source, Task<string> fault,
        Func<VideoFrame, bool>? predicate = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            if (fault.IsCompletedSuccessfully) throw new InvalidOperationException(await fault.ConfigureAwait(false));
            if (source.LatestFrame is { } frame && (predicate == null || predicate(frame))) return frame;
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new TimeoutException($"WGC did not publish the requested frame within 12 seconds. Latest: {source.LatestFrame?.Width}×{source.LatestFrame?.Height} sequence {source.LatestFrame?.Sequence}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
}
