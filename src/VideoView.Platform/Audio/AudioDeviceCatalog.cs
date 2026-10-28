using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VideoView.Platform.Audio;

public static class AudioDeviceCatalog
{
    /// <summary>Snapshot of active microphone endpoints. Does not start capture.</summary>
    public static IReadOnlyList<AudioSourceDescriptor> ListMicrophones() => OnMta(() =>
    {
        var result = new List<AudioSourceDescriptor>();
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        for (var index = 0; index < devices.Count; index++)
        {
            try
            {
                using var device = devices[index];
                result.Add(new AudioSourceDescriptor(device.ID, device.FriendlyName, AudioSourceKind.Microphone));
            }
            catch (COMException)
            {
                // An endpoint can disappear between enumeration and reading its properties.
            }
        }
        return Sort(result);
    });

    /// <summary>
    /// Snapshot of processes with active render sessions on any active output endpoint,
    /// deduplicated by PID. System sounds and sessions whose processes have exited are omitted.
    /// </summary>
    public static IReadOnlyList<AudioSourceDescriptor> ListApplications() => OnMta(() =>
    {
        var result = new Dictionary<int, AudioSourceDescriptor>();
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            try
            {
                using var device = devices[deviceIndex];
                var sessions = device.AudioSessionManager.Sessions;
                // NAudio returns null when QueryInterface(IAudioSessionManager2) is denied
                // (for example in a restricted process), rather than throwing that HRESULT.
                if (sessions is null)
                    continue;
                for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
                {
                    try
                    {
                        using var session = sessions[sessionIndex];
                        if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession)
                            continue;
                        var nativePid = session.GetProcessID;
                        if (nativePid == 0 || nativePid > int.MaxValue)
                            continue;
                        var pid = (int)nativePid;
                        if (result.ContainsKey(pid))
                            continue;
                        using var process = Process.GetProcessById(pid);
                        if (process.HasExited)
                            continue;
                        var name = GetFriendlyName(process);
                        result.Add(pid, new AudioSourceDescriptor(
                            "process:" + pid.ToString(CultureInfo.InvariantCulture), name,
                            AudioSourceKind.Application, pid));
                    }
                    catch (Exception exception) when (IsEnumerationRace(exception))
                    {
                        // Session/process lifetime is independent of this snapshot.
                    }
                }
            }
            catch (Exception exception) when (IsEnumerationRace(exception))
            {
                // Continue enumerating other endpoints after a device is removed.
            }
        }
        return Sort(result.Values);
    });

    private static string GetFriendlyName(Process process)
    {
        var name = process.ProcessName;
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
                name = description;
        }
        catch (Exception exception) when (IsEnumerationRace(exception) || exception is NotSupportedException)
        {
            // Protected/elevated processes can still have accessible audio sessions.
        }
        return name;
    }

    private static bool IsEnumerationRace(Exception exception) =>
        exception is COMException or Win32Exception or InvalidOperationException or ArgumentException;

    private static IReadOnlyList<AudioSourceDescriptor> Sort(IEnumerable<AudioSourceDescriptor> sources) =>
        sources.OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(source => source.Id, StringComparer.Ordinal).ToArray();

    private static T OnMta<T>(Func<T> action)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WASAPI audio enumeration requires Windows.");
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            return action();

        // IAudioSessionManager2 enumeration must run in an MTA, including calls from WPF.
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }) { IsBackground = true, Name = "VideoView audio catalog" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
