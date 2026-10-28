using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace VideoView.App;

internal sealed class GlobalHotkey : IDisposable
{
    private int _nextId = 0x5649;
    private Dictionary<int, ((uint Modifiers, uint Key) Gesture, HotkeyAction Action)> _bindings = [];
    private readonly nint _handle;
    private readonly HwndSource _source;
    public event Action<HotkeyAction>? Pressed;
    public GlobalHotkey(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle)!;
        _source.AddHook(Hook);
    }
    public void Register(VideoView.Core.AppSettings settings)
    {
        var requested = new[] { (settings.RecordHotkey, HotkeyAction.Record),
            (settings.BufferHotkey, HotkeyAction.Buffer), (settings.ReplayHotkey, HotkeyAction.SaveReplay),
            (settings.BookmarkHotkey, HotkeyAction.Bookmark) }
            .Select(item => (Gesture: Parse(item.Item1), Action: item.Item2, Text: item.Item1)).ToArray();
        if (requested.Select(item => item.Gesture).Distinct().Count() != requested.Length)
            throw new ArgumentException("각 동작에는 서로 다른 단축키를 지정해 주세요.");
        var next = new Dictionary<int, ((uint, uint), HotkeyAction)>();
        var added = new List<int>();
        try
        {
            foreach (var item in requested)
            {
                var existing = _bindings.FirstOrDefault(binding => binding.Value.Gesture == item.Gesture);
                var id = existing.Key != 0 ? existing.Key : ++_nextId;
                if (existing.Key == 0)
                {
                    if (!RegisterHotKey(_handle, id, item.Gesture.Modifiers | 0x4000, item.Gesture.Key))
                        throw new InvalidOperationException($"{item.Text} 등록 실패. 다른 프로그램에서 사용 중일 수 있습니다. 기존 단축키는 유지됩니다.");
                    added.Add(id);
                }
                next.Add(id, (item.Gesture, item.Action));
            }
        }
        catch { foreach (var id in added) UnregisterHotKey(_handle, id); throw; }
        foreach (var id in _bindings.Keys.Except(next.Keys)) UnregisterHotKey(_handle, id);
        _bindings = next;
    }
    public static (uint Modifiers, uint Key) Parse(string shortcut)
    {
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("단축키 예: Ctrl+Shift+F8. 보조키를 하나 이상 포함해 주세요.");
        uint modifiers = 0;
        foreach (var part in parts[..^1]) modifiers |= part.ToUpperInvariant() switch
        { "CTRL" or "CONTROL" => 2u, "ALT" => 1u, "SHIFT" => 4u, "WIN" => 8u, _ => throw new ArgumentException("보조키는 Ctrl·Alt·Shift·Win만 지원.") };
        if (!Enum.TryParse<System.Windows.Forms.Keys>(parts[^1], true, out var key) || (int)key <= 0 || (int)key > 255 ||
            key is System.Windows.Forms.Keys.ShiftKey or System.Windows.Forms.Keys.ControlKey or System.Windows.Forms.Keys.Menu or
            System.Windows.Forms.Keys.LWin or System.Windows.Forms.Keys.RWin)
            throw new ArgumentException("올바른 키 이름이 아님. 예: F8, R.");
        return (modifiers, (uint)key);
    }
    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    { if (msg == 0x0312 && _bindings.TryGetValue((int)wParam, out var binding)) { handled = true; Pressed?.Invoke(binding.Action); } return 0; }
    public void Dispose() { foreach (var id in _bindings.Keys) UnregisterHotKey(_handle, id); _bindings.Clear(); _source.RemoveHook(Hook); }
    public void Suspend() { foreach (var id in _bindings.Keys) UnregisterHotKey(_handle, id); _bindings.Clear(); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}

internal enum HotkeyAction { Record, Buffer, SaveReplay, Bookmark }

internal static class StartupRegistration
{
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로 확인 실패.");
            if (!Path.GetFileNameWithoutExtension(executable).Equals("VideoView", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("시작 프로그램 등록은 배포된 VideoView.exe에서 사용해 주세요.");
            key.SetValue("VideoView", $"\"{executable}\" --startup");
        }
        else key.DeleteValue("VideoView", false);
    }
}
