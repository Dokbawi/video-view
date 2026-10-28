using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;

namespace VideoView.Platform.Audio;

/// <summary>Windows 11 process-tree loopback, independent of any physical render endpoint.</summary>
internal static class ProcessLoopbackActivation
{
    // https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync
    // https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";

    public static AudioClient Activate(int processId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("Process audio capture requires Windows build 20348 or later.");

        cancellationToken.ThrowIfCancellationRequested();
        var parameters = new ActivationParameters
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = checked((uint)processId),
            ProcessLoopbackMode = 0 // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        var memory = Marshal.AllocCoTaskMem(Marshal.SizeOf<ActivationParameters>());
        var handler = new CompletionHandler();
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var submitted = false;
        var transferred = false;
        try
        {
            Marshal.StructureToPtr(parameters, memory, false);
            var variant = new BlobVariant
            {
                VariantType = 65, // VT_BLOB
                BlobSize = (uint)Marshal.SizeOf<ActivationParameters>(),
                BlobData = memory
            };
            var interfaceId = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                ProcessLoopbackDevice, ref interfaceId, ref variant, handler, out operation));
            submitted = true;

            // Windows provides no cancellation API. A timed-out or cancelled activation is
            // cleaned up by its eventual completion; its COM callback must remain alive.
            var nativeClient = handler.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .GetAwaiter().GetResult();
            var client = new AudioClient(nativeClient);
            transferred = true;
            return client;
        }
        finally
        {
            if (!submitted)
            {
                Marshal.FreeCoTaskMem(memory);
                Release(operation);
            }
            else
            {
                var pendingOperation = operation;
                var ownsClient = !transferred;
                _ = handler.Completion.Task.ContinueWith(task =>
                {
                    // Observe late errors as well as disposing a late successful activation.
                    if (task.IsCompletedSuccessfully && ownsClient)
                        Release(task.Result);
                    else if (task.IsFaulted)
                        _ = task.Exception;
                    Marshal.FreeCoTaskMem(memory);
                    Release(pendingOperation);
                    GC.KeepAlive(handler);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParameters
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    // PROPVARIANT's union starts at byte 8; BLOB's pointer is aligned to pointer size.
    // Sequential layout gives 24 bytes on x64 (pointer at 16), 16 on x86 (pointer at 12).
    [StructLayout(LayoutKind.Sequential)]
    private struct BlobVariant
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid interfaceId,
        ref BlobVariant activationParameters,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    // Like NAudio's own async activator, advertise the CCW as agile so Windows can
    // deliver completion on its MTA callback thread without an STA message pump.
    [ComImport, ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        internal TaskCompletionSource<IAudioClient> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            object? activated = null;
            try
            {
                operation.GetActivateResult(out var result, out activated);
                Marshal.ThrowExceptionForHR(result);
                var client = (IAudioClient)activated;
                if (Completion.TrySetResult(client))
                    activated = null; // Ownership passes to the waiting worker or late cleanup.
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
            finally
            {
                Release(activated);
            }
        }
    }
}
