using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VideoView.Core.Media;
using Pen = System.Windows.Media.Pen;
using Cursors = System.Windows.Input.Cursors;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace VideoView.App.Controls;

public sealed class ClipTimeline : FrameworkElement
{
    private double _duration = 1, _start, _end = 1, _position, _viewStart, _viewEnd = 1;
    private int _drag;
    private double _anchor, _originalStart, _originalEnd;
    private IReadOnlyList<AudioWaveform> _waveforms = [];
    private IReadOnlyList<double> _bookmarks = [];
    public IReadOnlyList<double> Bookmarks { get => _bookmarks; set { _bookmarks = value; InvalidateVisual(); } }
    private static readonly Brush BackgroundBrush = Frozen("#151C23"), Accent = Frozen("#59DEC1"), Muted = Frozen("#8297AA");
    private static readonly Brush Selection = Frozen("#143D38"), Shade = Frozen("#BB0A1016"), TextBrush = Frozen("#E8F3F4");
    public event Action? EditStarted;
    public event Action? RangeChanged;
    public event Action<double>? SeekRequested;
    public bool IsDragging => _drag != 0;
    public double Duration => _duration;
    public double SelectionStart => _start;
    public double SelectionEnd => _end;
    public double Position { get => _position; set { _position = Math.Clamp(value, 0, _duration); InvalidateVisual(); } }
    public IReadOnlyList<AudioWaveform> Waveforms { get => _waveforms; set { _waveforms = value; InvalidateVisual(); } }

    public ClipTimeline() { Focusable = true; ClipToBounds = true; Cursor = Cursors.Hand; }
    public void Reset(double duration)
    {
        _duration = Math.Max(0.001, duration); _start = _position = _viewStart = 0; _end = _viewEnd = _duration;
        _waveforms = []; _bookmarks = []; InvalidateVisual();
    }
    public void SetRange(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end)) return;
        var minimum = Math.Min(0.05, _duration);
        _start = Math.Clamp(start, 0, _duration - minimum);
        _end = Math.Clamp(end, _start + minimum, _duration);
        InvalidateVisual();
    }
    public void Zoom(double factor, double? anchor = null)
    {
        var center = anchor ?? Position;
        var length = Math.Clamp((_viewEnd - _viewStart) * factor, Math.Min(1, _duration), _duration);
        _viewStart = Math.Clamp(center - length / 2, 0, _duration - length); _viewEnd = _viewStart + length;
        InvalidateVisual();
    }
    public void Fit() { _viewStart = 0; _viewEnd = _duration; InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);
    public static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.ff" : @"mm\:ss\.ff");
    private double Span => Math.Max(0.001, _viewEnd - _viewStart);
    private double Pixels => Math.Max(1, ActualWidth - 24);
    private double X(double time) => 12 + (time - _viewStart) / Span * Pixels;
    private double Seconds(double x) => Math.Clamp(_viewStart + (x - 12) / Pixels * Span, 0, _duration);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 25 || ActualHeight < 40) return;
        var bottom = ActualHeight - 20;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.DrawRectangle(Selection, null, new Rect(Math.Clamp(X(_start), 12, ActualWidth - 12), 29,
            Math.Max(0, Math.Clamp(X(_end), 12, ActualWidth - 12) - Math.Clamp(X(_start), 12, ActualWidth - 12)), bottom - 29));
        var step = NiceStep(Span / Math.Max(2, Pixels / 100));
        for (var time = Math.Ceiling(_viewStart / step) * step; time <= _viewEnd; time += step)
        {
            var x = X(time);
            dc.DrawLine(new Pen(Frozen("#33414F"), 1), new(x, 23), new(x, bottom));
            Label(dc, Time(time).Split('.')[0], Math.Min(x + 3, ActualWidth - 52), 4, Muted, 10);
        }
        if (_waveforms.Count == 0) Label(dc, "시간축을 클릭해 탐색 · 양쪽 손잡이를 드래그해 자르기", 24, 49, Muted, 12);
        else
        {
            var rowHeight = (bottom - 32) / _waveforms.Count;
            for (var row = 0; row < _waveforms.Count; row++)
            {
                var wave = _waveforms[row]; var center = 32 + rowHeight * (row + 0.5);
                var pen = new Pen(Accent, 1.3);
                for (var x = 12d; x < ActualWidth - 12; x += 2)
                {
                    var a = Math.Clamp((int)(Seconds(x) / _duration * wave.Peaks.Length), 0, wave.Peaks.Length - 1);
                    var b = Math.Clamp((int)(Seconds(x + 2) / _duration * wave.Peaks.Length), a, wave.Peaks.Length - 1);
                    float peak = 0; for (var index = a; index <= b; index++) peak = Math.Max(peak, wave.Peaks[index]);
                    var amplitude = Math.Max(0.5, Math.Sqrt(peak) * Math.Max(1, rowHeight / 2 - 3));
                    dc.DrawLine(pen, new(x, center - amplitude), new(x, center + amplitude));
                }
            }
        }
        var left = Math.Clamp(X(_start), 12, ActualWidth - 12); var right = Math.Clamp(X(_end), 12, ActualWidth - 12);
        dc.DrawRectangle(Shade, null, new Rect(12, 29, Math.Max(0, left - 12), bottom - 29));
        dc.DrawRectangle(Shade, null, new Rect(right, 29, Math.Max(0, ActualWidth - 12 - right), bottom - 29));
        for (var row = 0; row < _waveforms.Count; row++)
        {
            var center = 32 + (bottom - 32) / _waveforms.Count * (row + 0.5);
            var name = _waveforms[row].Name;
            dc.DrawRectangle(BackgroundBrush, null, new Rect(17, center - 9, Math.Min(130, Pixels / 4), 18));
            Label(dc, name.Length > 18 ? name[..18] + "…" : name, 22, center - 8, Muted, 10);
        }
        dc.DrawRectangle(null, new Pen(Accent, 1), new Rect(left, 29, Math.Max(0, right - left), bottom - 29));
        foreach (var time in new[] { _start, _end })
            if (time >= _viewStart && time <= _viewEnd)
            {
                var x = X(time);
                dc.DrawRoundedRectangle(Accent, null, new Rect(x - 4, 27, 8, bottom - 25), 2, 2);
                dc.DrawLine(new Pen(BackgroundBrush, 1), new(x, (bottom + 27) / 2 - 7), new(x, (bottom + 27) / 2 + 7));
            }
        foreach (var mark in _bookmarks.Where(p => p >= _viewStart && p <= _viewEnd))
        {
            var x = X(mark);
            dc.DrawLine(new Pen(Frozen("#E6B865"), 1) { DashStyle = DashStyles.Dot }, new(x, 29), new(x, bottom));
            Label(dc, "◆", x - 5, 17, Frozen("#E6B865"), 10);
        }
        if (_position >= _viewStart && _position <= _viewEnd)
        {
            var x = X(_position);
            dc.DrawLine(new Pen(TextBrush, 1.5), new(x, 21), new(x, bottom + 4));
            dc.DrawEllipse(TextBrush, null, new(x, 23), 3.5, 3.5);
        }
        Label(dc, "Shift+드래그  구간 이동", 12, bottom + 5, Muted, 10);
        Label(dc, $"보기 {Time(_viewStart)} — {Time(_viewEnd)}", Math.Max(180, ActualWidth - 235), bottom + 5, Muted, 10);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        var x = e.GetPosition(this).X;
        if (e.GetPosition(this).Y <= 30)
        {
            var mark = _bookmarks.Where(p => p >= _viewStart && p <= _viewEnd && Math.Abs(X(p) - x) < 8).Cast<double?>().FirstOrDefault();
            if (mark.HasValue) { Position = mark.Value; SeekRequested?.Invoke(mark.Value); e.Handled = true; return; }
        }
        _drag = Math.Abs(x - X(_start)) <= 10 ? 1 : Math.Abs(x - X(_end)) <= 10 ? 2 :
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && Seconds(x) >= _start && Seconds(x) <= _end ? 3 : 4;
        _anchor = Seconds(x); _originalStart = _start; _originalEnd = _end;
        if (_drag != 4) EditStarted?.Invoke();
        CaptureMouse(); Move(x); e.Handled = true;
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        Cursor = _drag is 1 or 2 || Math.Abs(x - X(_start)) <= 10 || Math.Abs(x - X(_end)) <= 10 ? Cursors.SizeWE : Cursors.Hand;
        if (_drag != 0) Move(x);
        base.OnMouseMove(e);
    }
    private void Move(double x)
    {
        var time = Seconds(x); var minimum = Math.Min(0.05, _duration);
        if (_drag == 1) SetRange(Math.Min(time, _end - minimum), _end);
        else if (_drag == 2) SetRange(_start, Math.Max(time, _start + minimum));
        else if (_drag == 3)
        {
            var delta = Math.Clamp(time - _anchor, -_originalStart, _duration - _originalEnd);
            SetRange(_originalStart + delta, _originalEnd + delta);
        }
        else { Position = time; SeekRequested?.Invoke(time); return; }
        RangeChanged?.Invoke();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { _drag = 0; ReleaseMouseCapture(); base.OnMouseLeftButtonUp(e); e.Handled = true; }
    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e) { _drag = 0; base.OnLostMouseCapture(e); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    { Zoom(e.Delta > 0 ? 0.75 : 1.33333, Seconds(e.GetPosition(this).X)); e.Handled = true; }
    private void Label(DrawingContext dc, string text, double x, double y, Brush brush, double size) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Malgun Gothic"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    private static double NiceStep(double value)
    { var power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(0.001, value)))); var ratio = value / power; return power * (ratio <= 1 ? 1 : ratio <= 2 ? 2 : ratio <= 5 ? 5 : 10); }
    private static Brush Frozen(string hex) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; brush.Freeze(); return brush; }

    private sealed class TimelinePeer(ClipTimeline owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        protected override string GetClassNameCore() => "ClipTimeline";
        protected override string GetNameCore() => "클립 시간축";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => 5;
        public double SmallChange => 1d / 30;
        public double Maximum => owner.Duration;
        public double Minimum => 0;
        public double Value => owner.Position;
        public void SetValue(double value) => owner.Dispatcher.Invoke(() =>
        {
            if (!owner.IsEnabled) throw new InvalidOperationException("Timeline is busy.");
            if (!double.IsFinite(value) || value < 0 || value > owner.Duration) throw new ArgumentOutOfRangeException(nameof(value));
            owner.Position = value; owner.SeekRequested?.Invoke(value);
        });
    }
}
