using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace VideoView.Platform.Video;

internal static class NativeCapture
{
    private static readonly Guid CaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiInterfaceAccessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // IGraphicsCaptureItemInterop derives from IUnknown, not IInspectable. Slots 3/4 are
    // CreateForWindow/CreateForMonitor. Raw ABI calls avoid legacy RCW/CsWinRT mixing.
    internal static unsafe GraphicsCaptureItem CreateItem(CaptureTarget target)
    {
        const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint className = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out className));
            Guid factoryId = CaptureItemInteropId;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, in factoryId, out factory));
            int slot = target.Kind switch
            {
                CaptureTargetKind.Window => 3,
                CaptureTargetKind.Monitor => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(target))
            };
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[slot];
            Guid itemId = CaptureItemId;
            Marshal.ThrowExceptionForHR(create(factory, target.Handle, &itemId, &item));
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (className != 0) WindowsDeleteString(className);
        }
    }

    internal static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        nint inspectable = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out inspectable));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
        }
    }

    internal static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        using var surfaceReference = MarshalInterface<IDirect3DSurface>.CreateMarshaler(surface);
        Guid accessId = DxgiInterfaceAccessId;
        nint access = 0, texture = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfaceReference.ThisPtr, in accessId, out access));
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            Guid textureId = typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
            var result = new ID3D11Texture2D(texture); // Vortice takes ownership of this reference.
            texture = 0;
            return result;
        }
        finally
        {
            if (texture != 0) Marshal.Release(texture);
            if (access != 0) Marshal.Release(access);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        public static MonitorInfo Create() => new() { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
    }

    internal delegate bool WindowEnumProc(nint window, nint data);
    internal delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(WindowEnumProc callback, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();
    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("combase.dll")]
    internal static extern int RoInitialize(uint initializationType);
    [DllImport("combase.dll")]
    internal static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint className, in Guid iid, out nint factory);
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
