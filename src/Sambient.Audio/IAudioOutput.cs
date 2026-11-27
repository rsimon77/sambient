namespace Sambient.Audio;

/// <summary>Called on the realtime audio thread. Must not allocate or lock.</summary>
public interface IAudioCallback
{
    void Render(Span<float> interleavedStereo, int frames);
}

public sealed record AudioDeviceInfo(int Id, string Name, double DefaultSampleRate, int MaxOutputChannels, bool IsDefault);

public interface IAudioOutput : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    void Open(int deviceId, int sampleRate, int framesPerBuffer, IAudioCallback callback);
    void Start();
    void Stop();

    bool IsOpen { get; }
    bool IsRunning { get; }
    int SampleRate { get; }
    int FramesPerBuffer { get; }
    /// <summary>Output latency reported by the driver, in milliseconds.</summary>
    double OutputLatencyMs { get; }
}
