using System;

namespace VideoView.Platform.Audio;

public enum AudioSourceKind
{
    Microphone,
    System,
    Application
}

/// <param name="Id">Microphone endpoint ID, application identifier, or any system-source identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">The capture scope.</param>
/// <param name="ProcessId">Required for application capture; includes the target's child processes.</param>
public record AudioSourceDescriptor(string Id, string Name, AudioSourceKind Kind, int? ProcessId = null);

/// <summary>Live, interleaved left/right, 48,000 Hz IEEE float PCM. No file is recorded.</summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }

    /// <summary>Raised on a background thread for startup, capture, or disconnect failures.</summary>
    event Action<string>? Faulted;

    /// <summary>Recent absolute sample peak, between zero and one, before external gain/mute.</summary>
    float Peak { get; }

    /// <summary>Starts capture asynchronously. Repeated calls while running are harmless.</summary>
    void Start();

    /// <summary>
    /// Always returns count (individual samples, not frames). Never waits for incoming audio;
    /// unavailable samples are zero, including before Start and after Stop or Dispose.
    /// Call from a single master-clock consumer, normally in 960-sample (10 ms) blocks.
    /// The capture queue retains at most 40 ms and recovers to 20 ms after an overrun.
    /// </summary>
    int Read(float[] buffer, int offset, int count);

    /// <summary>Stops capture and discards pending samples. May be restarted until disposed.</summary>
    void Stop();
}

public static class AudioSourceFactory
{
    /// <summary>
    /// Creates an idle source. System capture uses the default multimedia render endpoint
    /// selected at Start; restart to pick up a different default. Application capture never
    /// falls back to system capture.
    /// </summary>
    public static IAudioSource Create(AudioSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        if (!Enum.IsDefined(descriptor.Kind))
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Unknown audio source kind.");
        if (descriptor.Kind == AudioSourceKind.Microphone)
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);
        if (descriptor.Kind == AudioSourceKind.Application && descriptor.ProcessId is not > 0)
            throw new ArgumentException("Application capture requires a positive ProcessId.", nameof(descriptor));

        return new WasapiAudioSource(descriptor);
    }
}
