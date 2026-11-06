using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace VideoView.Platform.Video;

/// <summary>Captures exactly one selected HWND/HMONITOR with Windows Graphics Capture.</summary>
/// <remarks>
/// Start reports setup failures synchronously. Runtime failures (including target close)
/// stop capture, clear LatestFrame, and raise Faulted on a thread-pool thread. Marshal UI updates
/// to the UI thread. Resize recreates the frame pool without replacing the selected capture item.
/// </remarks>
public sealed class ScreenCaptureSource : IDisposable
{
    private readonly object _lifecycle = new();
    private readonly CaptureTarget _target;
    private readonly int _framesPerSecond;
    private readonly bool _hideBorder, _captureCursor;
    public bool BorderlessAccessGranted { get; private set; }
    public string CaptureNotice { get; private set; } = "";
    private CaptureRun? _run;
    private long _sequence;
    private bool _disposed;

    public ScreenCaptureSource(CaptureTarget target, int framesPerSecond = 30,
        bool hideBorder = true, bool captureCursor = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Handle == 0) throw new ArgumentException("A nonzero native capture handle is required.", nameof(target));
        if (!Enum.IsDefined(target.Kind)) throw new ArgumentOutOfRangeException(nameof(target));
        _target = target;
        if (framesPerSecond is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        _framesPerSecond = framesPerSecond;
        _hideBorder = hideBorder; _captureCursor = captureCursor;
    }

    public event Action<string>? Faulted;

    /// <summary>The latest complete snapshot, or null before the first frame and after stop/failure.</summary>
    public VideoFrame? LatestFrame => Volatile.Read(ref _run)?.LatestFrame;
    public VideoFrameLease? AcquireLatestFrame() => Volatile.Read(ref _run)?.Frames.Acquire();

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is { IsFinished: false }) return;
            _run?.Dispose();
            var run = new CaptureRun(this, _target);
            Volatile.Write(ref _run, run);
            try { run.Start(); }
            catch
            {
                Volatile.Write(ref _run, null);
                run.Dispose();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            var run = _run;
            Volatile.Write(ref _run, null);
            run?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    private void ReportFault(CaptureRun run, string message)
    {
        // Never invoke consumers on the capture thread: a handler may call Stop and join it.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!ReferenceEquals(Volatile.Read(ref _run), run)) return;
            var handlers = Faulted;
            if (handlers is null) return;
            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try { handler(message); }
                catch (Exception ex) { Trace.TraceError($"Capture fault handler failed: {ex}"); }
            }
        });
    }

    private sealed class CaptureRun : IDisposable
    {
        private const int BufferCount = 2;
        private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
        private readonly ScreenCaptureSource _owner;
        private readonly CaptureTarget _target;
        private readonly AutoResetEvent _frameReady = new(false);
        private readonly ManualResetEvent _stop = new(false);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        internal CaptureFrames Frames { get; } = new();
        private bool _nativePacing;
        private string? _failure;
        private int _finished;
        private bool _threadStarted;
        private bool? _borderlessAllowed;

        public CaptureRun(ScreenCaptureSource owner, CaptureTarget target)
        {
            _owner = owner;
            _target = target;
            _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "VideoView WGC capture" };
            _thread.SetApartmentState(ApartmentState.MTA);
        }

        public VideoFrame? LatestFrame => Frames.Snapshot();
        public bool IsFinished => Volatile.Read(ref _finished) != 0;

        public void Start()
        {
            _thread.Start();
            _threadStarted = true;
            _started.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Signal(_stop);
            if (_threadStarted) _thread.Join();
            _frameReady.Dispose();
            _stop.Dispose();
        }

        private void CaptureLoop()
        {
            GraphicsCaptureItem? item = null;
            Direct3D11CaptureFramePool? pool = null;
            GraphicsCaptureSession? session = null;
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            IDirect3DDevice? winrtDevice = null;
            ID3D11Texture2D? staging = null;
            bool initialized = false, started = false;
            try
            {
                Marshal.ThrowExceptionForHR(NativeCapture.RoInitialize(1 /* RO_INIT_MULTITHREADED */));
                initialized = true;
                if (!GraphicsCaptureSession.IsSupported())
                    throw new NotSupportedException("Windows Graphics Capture is unavailable on this device.");
                ValidateTarget();
                item = NativeCapture.CreateItem(_target);
                item.Closed += OnItemClosed;
                SizeInt32 size = item.Size;
                if (size.Width <= 0 || size.Height <= 0)
                    throw new InvalidOperationException("The capture target has no drawable area. Restore the window and try again.");
                _ = checked(size.Width * size.Height * 4);

                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                context = device.ImmediateContext;
                using (var multithread = context.QueryInterface<ID3D11Multithread>())
                    multithread.SetMultithreadProtected(true);
                winrtDevice = NativeCapture.CreateWinRtDevice(device);
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, PixelFormat, BufferCount, size);
                pool.FrameArrived += OnFrameArrived;
                session = StartCapture(pool, item);
                if (Volatile.Read(ref _failure) is { } initialFailure)
                    throw new InvalidOperationException(initialFailure);
                started = true;
                _started.TrySetResult();

                WaitHandle[] signals = [_stop, _frameReady];
                long resizePendingAt = 0;
                long nextRead = 0;
                var interval = Stopwatch.Frequency / _owner._framesPerSecond;
                while (true)
                {
                    int signaled = WaitHandle.WaitAny(signals, 250);
                    if (signaled == 0) break;
                    if (signaled == WaitHandle.WaitTimeout)
                    {
                        // Some offscreen/minimized targets stop submitting frames before WGC
                        // delivers Closed. Detect a destroyed HWND/disconnected monitor as well.
                        ValidateTarget();
                        // Static/occluded windows occasionally provide no fresh surface after
                        // Recreate. Reconnect only the same item; encoder/audio clocks stay alive.
                        if (resizePendingAt != 0 && Stopwatch.GetElapsedTime(resizePendingAt).TotalMilliseconds >= 250)
                        {
                            session.Dispose(); session = null;
                            pool.FrameArrived -= OnFrameArrived;
                            pool.Dispose();
                            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, PixelFormat, BufferCount, size);
                            pool.FrameArrived += OnFrameArrived;
                            session = StartCapture(pool, item);
                            resizePendingAt = 0;
                        }
                        continue;
                    }
                    // Older Windows versions lack MinUpdateInterval. Wait before draining, so
                    // the final repaint of a now-static window isn't discarded by throttling.
                    var remaining = nextRead - Stopwatch.GetTimestamp();
                    if (!_nativePacing && remaining > 0 &&
                        _stop.WaitOne(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency))) break;
                    nextRead = Stopwatch.GetTimestamp() + interval;
                    {
                        SizeInt32? resized = null;
                        bool shrinkPublished = false;
                        using (var frame = TakeNewestFrame(pool))
                        {
                            if (frame is null) continue;
                            var content = frame.ContentSize;
                            // Minimize/restore may transiently report zero size. Retain the last image.
                            if (content.Width <= 0 || content.Height <= 0) continue;
                            if (content.Width != size.Width || content.Height != size.Height)
                            {
                                resized = content;
                                // A shrinking window already fits in the old surface. Publish its
                                // valid content before recreation; a static window may not repaint.
                                if (content.Width <= size.Width && content.Height <= size.Height)
                                {
                                    shrinkPublished = ReadFrame(frame, content, device, context, ref staging);
                                }
                            }
                            else
                            {
                                ReadFrame(frame, size, device, context, ref staging);
                                resizePendingAt = 0;
                            }
                        }
                        // Retire the old frame before Recreate invalidates pooled surfaces.
                        if (resized is { } next && !_stop.WaitOne(0))
                        {
                            _ = checked(next.Width * next.Height * 4);
                            staging?.Dispose(); staging = null;
                            pool.Recreate(winrtDevice, PixelFormat, BufferCount, next);
                            size = next;
                            resizePendingAt = shrinkPublished ? 0 : Stopwatch.GetTimestamp();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure,
                    $"Capture of '{_target.Name}' failed: {ex.Message} (0x{ex.HResult:X8})", null);
                _started.TrySetException(new InvalidOperationException(_failure, ex));
            }
            finally
            {
                // Event handlers only signal wait handles, so Close cannot deadlock on capture locks.
                Frames.Clear();
                if (item is not null) Cleanup(() => item.Closed -= OnItemClosed);
                if (pool is not null) Cleanup(() => pool.FrameArrived -= OnFrameArrived);
                if (session is not null) Cleanup(session.Dispose);
                if (pool is not null) Cleanup(pool.Dispose);
                if (staging is not null) Cleanup(staging.Dispose);
                if (winrtDevice is not null) Cleanup(winrtDevice.Dispose);
                if (context is not null) Cleanup(context.Dispose);
                if (device is not null) Cleanup(device.Dispose);
                if (initialized) NativeCapture.RoUninitialize();
                Volatile.Write(ref _finished, 1);
                if (started && Volatile.Read(ref _failure) is { } failure)
                    _owner.ReportFault(this, failure);
            }
        }

        private GraphicsCaptureSession StartCapture(Direct3D11CaptureFramePool pool, GraphicsCaptureItem item)
        {
            var session = pool.CreateCaptureSession(item);
            try
            {
                session.IsCursorCaptureEnabled = _owner._captureCursor;
                if (_owner._hideBorder)
                {
                    try
                    {
                        // Request access once per run on this MTA thread, also used by OBS's Win32 capture.
                        // Reconnecting the same item after resize reuses consent, never widens capture.
                        _borderlessAllowed ??= GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                            .AsTask().GetAwaiter().GetResult() == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
                        if (_borderlessAllowed == true) session.IsBorderRequired = false;
                        _owner.BorderlessAccessGranted = _borderlessAllowed == true && !session.IsBorderRequired;
                        _owner.CaptureNotice = _owner.BorderlessAccessGranted ? "" :
                            "Windows에서 테두리 숨김을 허용하지 않았습니다. 캡처는 계속됩니다.";
                    }
                    catch (Exception error) when (error is COMException or UnauthorizedAccessException or NotSupportedException)
                    {
                        _borderlessAllowed = false; _owner.BorderlessAccessGranted = false;
                        _owner.CaptureNotice = "테두리를 숨길 수 없어 Windows 캡처 표시를 유지합니다.";
                    }
                }
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
                    Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
                {
                    try
                    {
                        session.MinUpdateInterval = TimeSpan.FromSeconds(1d / _owner._framesPerSecond);
                        _nativePacing = true;
                    }
                    catch (Exception error) when (error is COMException or NotSupportedException)
                    { _nativePacing = false; Trace.WriteLine("Using capture-side frame pacing: " + error.Message); }
                }
                session.StartCapture();
                return session;
            }
            catch { session.Dispose(); throw; }
        }

        private static Direct3D11CaptureFrame? TakeNewestFrame(Direct3D11CaptureFramePool pool)
        {
            var frame = pool.TryGetNextFrame();
            if (frame == null) return null;
            try
            {
                var next = pool.TryGetNextFrame(); // two pooled surfaces in total
                if (next == null) return frame;
                frame.Dispose();
                return next;
            }
            catch { frame.Dispose(); throw; }
        }

        private bool ReadFrame(Direct3D11CaptureFrame frame, SizeInt32 size, ID3D11Device device,
            ID3D11DeviceContext context, ref ID3D11Texture2D? staging)
        {
            int stride = checked(size.Width * 4);
            var slot = Frames.BeginWrite(checked(stride * size.Height));
            if (slot == null) return false; // all readers busy; keep the last complete frame
            using var texture = NativeCapture.GetTexture(frame.Surface);
            var description = texture.Description;
            if (description.Format != Format.B8G8R8A8_UNorm || description.Width < size.Width ||
                description.Height < size.Height || description.SampleDescription.Count != 1 ||
                description.ArraySize != 1 || description.MipLevels != 1)
                throw new InvalidOperationException("WGC returned an incompatible BGRA surface.");
            if (staging is null || staging.Description.Width != description.Width || staging.Description.Height != description.Height)
            {
                staging?.Dispose();
                staging = null;
                description.Usage = ResourceUsage.Staging;
                description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.Read;
                description.MiscFlags = ResourceOptionFlags.None;
                staging = device.CreateTexture2D(description);
            }
            context.CopyResource(staging, texture);
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowPitch = checked((int)mapped.RowPitch);
                if (rowPitch < stride || mapped.DataPointer == 0)
                    throw new InvalidOperationException("D3D11 returned an invalid mapped surface.");
                if (rowPitch == stride)
                    Marshal.Copy(mapped.DataPointer, slot.Pixels, 0, slot.Pixels.Length);
                else for (int y = 0; y < size.Height; y++)
                    Marshal.Copy(mapped.DataPointer + checked(y * rowPitch), slot.Pixels, y * stride, stride);
                Frames.Publish(slot, size.Width, size.Height, Interlocked.Increment(ref _owner._sequence));
                return true;
            }
            finally { context.Unmap(staging, 0); }
        }

        private void ValidateTarget()
        {
            if (_target.Kind == CaptureTargetKind.Window)
            {
                if (!NativeCapture.IsWindow(_target.Handle))
                    throw new InvalidOperationException("The selected window was closed or no longer exists.");
                NativeCapture.GetWindowThreadProcessId(_target.Handle, out uint process);
                // Catalog IDs contain a PID so handle reuse by a different process is rejected.
                if (_target.Id.StartsWith("window:", StringComparison.Ordinal) &&
                    _target.Id != $"window:{process}:{_target.Handle:X}")
                    throw new InvalidOperationException("The selected window handle has been reused. Select the target again.");
            }
            else
            {
                var info = NativeCapture.MonitorInfo.Create();
                if (!NativeCapture.GetMonitorInfo(_target.Handle, ref info) ||
                    (_target.Id.StartsWith("monitor:", StringComparison.Ordinal) && _target.Id != $"monitor:{info.Device}"))
                    throw new InvalidOperationException("The selected monitor is no longer available. Select the target again.");
            }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args) => Signal(_frameReady);

        private void OnItemClosed(GraphicsCaptureItem sender, object args)
        {
            Interlocked.CompareExchange(ref _failure, $"Capture target '{_target.Name}' was closed or disconnected.", null);
            Signal(_stop);
        }

        private static void Signal(EventWaitHandle signal)
        {
            // A callback already dispatched by WinRT can finish after event unsubscription.
            try { signal.Set(); }
            catch (ObjectDisposedException) { }
        }

        private void Cleanup(Action dispose)
        {
            try { dispose(); }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, $"Capture cleanup failed: {ex.Message}", null);
            }
        }
    }
}
