using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoView.Platform.Video;

public static class CaptureTargetCatalog
{
    /// <summary>Lists monitors and visible, uncloaked top-level windows outside this process.</summary>
    /// <remarks>Uses Win32 metadata only. Does not create a capture item, device, or session.</remarks>
    public static IReadOnlyList<CaptureTarget> List()
    {
        var targets = new List<CaptureTarget>();
        NativeCapture.MonitorEnumProc monitorCallback = (nint monitor, nint dc, ref NativeCapture.Rect rect, nint data) =>
        {
            var info = NativeCapture.MonitorInfo.Create();
            if (NativeCapture.GetMonitorInfo(monitor, ref info))
            {
                string primary = (info.Flags & 1) != 0 ? " (primary)" : "";
                targets.Add(new CaptureTarget($"monitor:{info.Device}",
                    $"{info.Device} — {info.Monitor.Right - info.Monitor.Left}×{info.Monitor.Bottom - info.Monitor.Top}{primary}",
                    CaptureTargetKind.Monitor, monitor));
            }
            return true;
        };
        if (!NativeCapture.EnumDisplayMonitors(0, 0, monitorCallback, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate displays.");

        uint ownProcess = (uint)Environment.ProcessId;
        nint shell = NativeCapture.GetShellWindow();
        NativeCapture.WindowEnumProc windowCallback = (window, data) =>
        {
            if (window == shell || !NativeCapture.IsWindowVisible(window) ||
                NativeCapture.GetAncestor(window, 2 /* GA_ROOT */) != window)
                return true;

            NativeCapture.GetWindowThreadProcessId(window, out uint process);
            if (process == 0 || process == ownProcess)
                return true;

            if (NativeCapture.DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) >= 0 && cloaked != 0)
                return true;

            // Window titles may change or disappear between these calls. Enumeration stays best-effort.
            var title = new StringBuilder(Math.Clamp(NativeCapture.GetWindowTextLength(window) + 1, 1, 32768));
            NativeCapture.GetWindowText(window, title, title.Capacity);
            string name = title.ToString().Trim();
            if (name.Length == 0)
                name = $"Window 0x{window:X} (PID {process})";
            targets.Add(new CaptureTarget($"window:{process}:{window:X}", name, CaptureTargetKind.Window, window));
            return true;
        };
        if (!NativeCapture.EnumWindows(windowCallback, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate windows.");

        return targets.AsReadOnly();
    }
}
