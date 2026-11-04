using NAudio.CoreAudioApi;

namespace VideoView.Platform.Audio;

public sealed record OutputVolumeSnapshot(string Id, string Name, float Decibels, float Scalar, bool Muted,
    float MinimumDecibels, float MaximumDecibels, float IncrementDecibels);

/// <summary>Explicit, device-scoped reductions only. Never changes session volumes or unmutes an endpoint.</summary>
public sealed class ListeningVolumeService
{
    public IReadOnlyList<OutputVolumeSnapshot> ReadOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<OutputVolumeSnapshot>();
        string? defaultId = null;
        try { using var primary = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = primary.ID; }
        catch (System.Runtime.InteropServices.COMException) { /* No default endpoint; still list explicit outputs. */ }
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) result.Add(Read(device));
        }
        return result.OrderByDescending(device => device.Id == defaultId).ThenBy(device => device.Name).ToArray();
    }

    public OutputVolumeSnapshot Reduce(OutputVolumeSnapshot expected, double reductionDecibels)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(expected.Id);
        if (device.State != DeviceState.Active) throw new InvalidOperationException("출력 장치가 연결되어 있지 않습니다. 새로고침하세요.");
        var current = Read(device);
        var target = ReducedTarget(expected, current, reductionDecibels);
        // There is deliberately no restore-on-exit, auto-start action, session write, or unmute.
        device.AudioEndpointVolume.MasterVolumeLevel = target;
        return Read(device);
    }

    public static float ReducedTarget(OutputVolumeSnapshot expected, OutputVolumeSnapshot current, double reductionDecibels)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);
        if (!double.IsFinite(reductionDecibels) || reductionDecibels is <= 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(reductionDecibels));
        if (expected.Id != current.Id || expected.Muted != current.Muted ||
            !float.IsFinite(expected.Decibels) || !float.IsFinite(current.Decibels) ||
            Math.Abs(expected.Decibels - current.Decibels) > 0.05)
            throw new InvalidOperationException("장치나 음량이 바뀌었습니다. 새로고침 후 다시 적용하세요.");
        if (!float.IsFinite(current.MinimumDecibels) || !float.IsFinite(current.MaximumDecibels) ||
            !float.IsFinite(current.IncrementDecibels) || current.IncrementDecibels <= 0 ||
            current.MinimumDecibels > current.MaximumDecibels || current.Decibels < current.MinimumDecibels ||
            current.Decibels > current.MaximumDecibels)
            throw new InvalidOperationException("이 장치의 정밀 음량 범위를 확인할 수 없습니다. Windows 소리 설정을 사용하세요.");
        // Round down to a supported step, so rounding cannot increase the requested volume.
        var steps = Math.Floor((current.Decibels - reductionDecibels - current.MinimumDecibels) / current.IncrementDecibels);
        var target = Math.Max(current.MinimumDecibels, current.MinimumDecibels + steps * current.IncrementDecibels);
        if (target >= current.Decibels - 0.001)
            throw new InvalidOperationException("이 장치가 지원하는 최소 음량입니다. 더 낮출 수 없습니다.");
        return (float)target;
    }

    private static OutputVolumeSnapshot Read(MMDevice device)
    {
        var volume = device.AudioEndpointVolume;
        var range = volume.VolumeRange;
        return new(device.ID, device.FriendlyName, volume.MasterVolumeLevel, volume.MasterVolumeLevelScalar, volume.Mute,
            range.MinDecibels, range.MaxDecibels, range.IncrementDecibels);
    }
}
