using System.Runtime.InteropServices;
using System.Windows.Interop;
using VideoView.App;
using VideoView.App.Controls;
using VideoView.Core;
using VideoView.Platform.Video;

namespace VideoView.Tests;

internal static class EditorTests
{
    public static Task RunAsync(Action<bool, string> assert)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var canvas = new FrameCanvas(8, 4);
                byte[] Solid(int width, int height) => Enumerable.Range(0, width * height).SelectMany(_ => new byte[] { 60, 120, 180, 255 }).ToArray();
                var source = new VideoFrame(Solid(2, 4), 2, 4, 1);
                var pixels = canvas.Fit(source);
                assert(pixels.Length == 128 && pixels[0] == 0 && pixels[3] == 255 && pixels[(1 * 8 + 3) * 4 + 1] == 120,
                    "Frame fitting preserves aspect ratio with opaque black side bars");
                var retained = pixels.ToArray(); canvas.Fit(new VideoFrame(Solid(8, 2), 8, 2, 2));
                assert(pixels[0] == 0 && pixels[(1 * 8) * 4 + 1] == 120 && source.Pixels.SequenceEqual(Solid(2, 4)),
                    "Repeated aspect changes clear stale margins and never mutate captured source arrays");
                var timeline = new ClipTimeline(); timeline.Reset(60); timeline.SetRange(-10, 100);
                assert(timeline.SelectionStart == 0 && timeline.SelectionEnd == 60, "Timeline trim cannot extend outside source bounds");
                timeline.SetRange(50, 20);
                assert(timeline.SelectionEnd > timeline.SelectionStart && timeline.SelectionEnd <= 60,
                    "Crossing trim handles cannot produce an inverted or empty export range");
                timeline.Reset(0.01); timeline.SetRange(0, 0.01);
                assert(timeline.SelectionEnd == 0.01 && ClipTimeline.Time(3661.25) == "1:01:01.25", "Short clips and hour-long timecodes remain representable");

                var window = new System.Windows.Window(); var blocker = new System.Windows.Window();
                try
                {
                    var handle = new WindowInteropHelper(window).EnsureHandle(); var other = new WindowInteropHelper(blocker).EnsureHandle();
                    using var hotkeys = new GlobalHotkey(window);
                    var settings = new AppSettings { RecordHotkey = "Ctrl+Alt+Shift+F20", BufferHotkey = "Ctrl+Alt+Shift+F21", ReplayHotkey = "Ctrl+Alt+Shift+F22", BookmarkHotkey = "Ctrl+Alt+Shift+F19" };
                    var actions = new List<HotkeyAction>(); hotkeys.Pressed += actions.Add;
                    hotkeys.Register(settings);
                    SendMessage(handle, 0x0312, 0x564A, 0); SendMessage(handle, 0x0312, 0x564B, 0); SendMessage(handle, 0x0312, 0x564C, 0);
                    SendMessage(handle, 0x0312, 0x564D, 0);
                    assert(actions.SequenceEqual([HotkeyAction.Record, HotkeyAction.Buffer, HotkeyAction.SaveReplay, HotkeyAction.Bookmark]), "Four registered native hotkeys dispatch distinct recording and bookmark actions");
                    (settings.RecordHotkey, settings.ReplayHotkey) = (settings.ReplayHotkey, settings.RecordHotkey);
                    hotkeys.Register(settings); SendMessage(handle, 0x0312, 0x564A, 0);
                    assert(actions[^1] == HotkeyAction.SaveReplay, "Swapping assigned gestures reuses registrations with the new actions");
                    var duplicate = new AppSettings { RecordHotkey = settings.RecordHotkey, BufferHotkey = settings.RecordHotkey, ReplayHotkey = settings.ReplayHotkey };
                    var rejected = false; try { hotkeys.Register(duplicate); } catch (ArgumentException) { rejected = true; }
                    assert(rejected, "Duplicate shortcuts are rejected before any native registration changes");
                    assert(RegisterHotKey(other, 77, 7, 0x87), "Reserve F24 on an owned hidden test window");
                    try
                    {
                        var conflict = new AppSettings { RecordHotkey = "Ctrl+Alt+Shift+F23", BufferHotkey = "Ctrl+Alt+Shift+F24", ReplayHotkey = settings.ReplayHotkey };
                        rejected = false; try { hotkeys.Register(conflict); } catch (InvalidOperationException) { rejected = true; }
                        SendMessage(handle, 0x0312, 0x564B, 0);
                        assert(rejected && actions[^1] == HotkeyAction.Buffer, "External hotkey conflict preserves every previously registered action");
                        assert(RegisterHotKey(other, 78, 7, 0x86), "Failed registration rolls back newly acquired keys");
                        UnregisterHotKey(other, 78);
                    }
                    finally { UnregisterHotKey(other, 77); }
                    hotkeys.Suspend(); hotkeys.Register(settings);
                    assert(true, "Shortcut editing can suspend and restore global registrations");
                }
                finally { window.Close(); blocker.Close(); }
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completed.Task;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
