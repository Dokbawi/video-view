param([switch]$Capture, [string]$ShadowDirectory)
$ErrorActionPreference = 'Stop'
$captureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$captureAssets = Get-Content -LiteralPath (Join-Path $captureRoot 'src\VideoView.Platform\obj\project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$capturePackageRoot = Join-Path $captureRoot '.tools\nuget'
foreach ($captureLibrary in $captureAssets.targets.Values[0].GetEnumerator()) {
    if ($captureLibrary.Value.type -ne 'package') { continue }
    foreach ($captureRuntime in $captureLibrary.Value.runtime.Keys) {
        if ($captureRuntime.EndsWith('.dll')) {
            $captureAssemblyPath = Join-Path $capturePackageRoot ($captureLibrary.Key.ToLowerInvariant() + '/' + $captureRuntime)
            [void][Reflection.Assembly]::LoadFrom($captureAssemblyPath)
        }
    }
}
$captureSdk = Get-ChildItem -LiteralPath (Join-Path $capturePackageRoot 'microsoft.windows.sdk.net.ref') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
foreach ($captureDll in @('WinRT.Runtime.dll', 'Microsoft.Windows.SDK.NET.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $captureSdk.FullName "lib\net8.0\$captureDll"))
}
$captureBuildAssembly = Join-Path $captureRoot 'src\VideoView.Platform\bin\Debug\net10.0-windows10.0.26100.0\VideoView.Platform.dll'
if (!$ShadowDirectory) { $ShadowDirectory = Join-Path ([IO.Path]::GetTempPath()) ('VideoView.WgcSmoke.' + [Guid]::NewGuid().ToString('N')) }
[void][IO.Directory]::CreateDirectory($ShadowDirectory)
$capturePlatformAssembly = Join-Path $ShadowDirectory 'VideoView.Platform.dll'
Copy-Item -LiteralPath $captureBuildAssembly -Destination $capturePlatformAssembly
[void][Reflection.Assembly]::LoadFrom($capturePlatformAssembly)
$captureTargets = [VideoView.Platform.Video.CaptureTargetCatalog]::List()
Write-Output ('Catalog: monitors={0}, windows={1}' -f @($captureTargets | Where-Object Kind -eq Monitor).Count, @($captureTargets | Where-Object Kind -eq Window).Count)
if (!$Capture) {
    Write-Output 'Enumeration only. Pass -Capture to test an owned offscreen window; no monitor/unrelated window is captured and no pixels are saved.'
    return
}

# Run with PowerShell 7.6+ (.NET 10), after building Platform. A desktop-capable token is required.
$captureReferences = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName) + $capturePlatformAssembly
Add-Type -ReferencedAssemblies $captureReferences -TypeDefinition @'
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VideoView.Platform.Video;

public static class WgcSmokeTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static VideoFrame WaitForFrame(ScreenCaptureSource source)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = source.LatestFrame;
            if (frame != null)
            {
                Check(frame.Pixels.Length == checked(frame.Width * frame.Height * 4), "Invalid BGRA tight stride.");
                return frame;
            }
            Thread.Sleep(20);
        }
        throw new TimeoutException("WGC produced no CPU frame within 12 seconds.");
    }

    public static void Run()
    {
        using (var invalid = new ScreenCaptureSource(new CaptureTarget("validation-invalid", "Invalid", CaptureTargetKind.Window, -1)))
        {
            bool rejected = false;
            try { invalid.Start(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && invalid.LatestFrame == null, "Invalid HWND was not rejected.");
        }

        using var window = new TestWindow();
        Check(!CaptureTargetCatalog.List().Any(t => t.Handle == window.Handle && t.Kind == CaptureTargetKind.Window),
            "Catalog included own-process window.");
        using var source = new ScreenCaptureSource(new CaptureTarget("validation", "WGC synthetic test", CaptureTargetKind.Window, window.Handle));
        using var faultReady = new ManualResetEventSlim();
        string fault = "";
        int faultCount = 0;
        source.Faulted += message =>
        {
            Volatile.Write(ref fault, message);
            Interlocked.Increment(ref faultCount);
            source.Stop(); // This must not deadlock against worker shutdown.
            faultReady.Set();
        };
        source.Start();
        source.Start();
        var first = WaitForFrame(source);
        var retained = first.Pixels.ToArray();
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 1000; i++)
            {
                var frame = source.LatestFrame;
                Check(frame != null && frame.Pixels.Length == frame.Width * frame.Height * 4,
                    "Concurrent reader observed incomplete frame.");
            }
        });
        source.Stop();
        source.Stop();
        Check(source.LatestFrame == null && first.Pixels.SequenceEqual(retained), "Stop invalidated retained snapshot.");
        source.Start();
        var restarted = WaitForFrame(source);
        Check(restarted.Sequence > first.Sequence && !ReferenceEquals(first.Pixels, restarted.Pixels), "Snapshot ownership/sequence failed.");
        Check(first.Pixels.SequenceEqual(retained), "Published snapshot was mutated after restart.");
        Console.WriteLine($"Window BGRA: {first.Width}x{first.Height}; ownership, concurrent reads, Stop/restart passed.");

        window.Resize();
        var resizeDeadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < resizeDeadline && source.LatestFrame is { } same && same.Width == first.Width && same.Height == first.Height)
            Thread.Sleep(20);
        var resized = source.LatestFrame;
        Check(resized != null && (resized.Width != first.Width || resized.Height != first.Height) && faultCount == 0,
            "Resize did not continue capture at the new size.");
        window.Dispose();
        Check(faultReady.Wait(TimeSpan.FromSeconds(12)), "Target close did not raise Faulted.");
        Check(Volatile.Read(ref fault).Contains("closed") && source.LatestFrame == null && faultCount == 1, "Close fault contract failed.");
        Console.WriteLine("Continuous resize and Closed fault passed; fault handler safely called Stop.");

        source.Dispose();
        bool disposedRejected = false;
        try { source.Start(); } catch (ObjectDisposedException) { disposedRejected = true; }
        Check(disposedRejected, "Disposed source restarted.");
        Console.WriteLine("PASS: WGC capture smoke tests; no pixel files written.");
    }

    private sealed class TestWindow : IDisposable
    {
        public nint Handle;
        private readonly Thread thread;
        private readonly ManualResetEventSlim ready = new();
        private Exception error;
        private bool disposed;
        public TestWindow()
        {
            thread = new Thread(() =>
            {
                // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; offscreen, visible, white STATIC control.
                Handle = CreateWindowEx(0x08000080, "STATIC", "WGC synthetic test", 0x90000006,
                    -10000, -10000, 96, 64, 0, 0, 0, 0);
                if (Handle == 0) error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                ready.Set();
                while (Handle != 0 && IsWindow(Handle) && GetMessage(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(in message);
                    DispatchMessage(in message);
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Check(ready.Wait(TimeSpan.FromSeconds(5)), "Test window startup timed out.");
            if (error != null) throw error;
        }
        public void Resize() => Check(SetWindowPos(Handle, 0, -10000, -10000, 128, 80, 0x0014), "Test window resize failed.");
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            PostMessage(Handle, 0x0010, 0, 0);
            Check(thread.Join(TimeSpan.FromSeconds(5)), "Test window shutdown timed out.");
            ready.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(in Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
'@
[WgcSmokeTest]::Run()
