using System.Runtime.InteropServices;
using PortAudioSharp;
using PAStream = PortAudioSharp.Stream;

namespace Sambient.Audio;

public sealed class PortAudioOutput : IAudioOutput
{
    private static readonly object InitLock = new();
    private static int _initCount;

    private PAStream? _stream;
    private IAudioCallback? _callback;
    private PAStream.Callback? _nativeCallback; // held in a field so the GC cannot collect the delegate
    private bool _disposed;

    public PortAudioOutput()
    {
        lock (InitLock)
        {
            if (_initCount++ == 0) PortAudio.Initialize();
        }
    }

    public bool IsOpen => _stream != null;
    public bool IsRunning { get; private set; }
    public int SampleRate { get; private set; }
    public int FramesPerBuffer { get; private set; }
    public double OutputLatencyMs { get; private set; }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        int def = PortAudio.DefaultOutputDevice;
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels <= 0) continue;
            list.Add(new AudioDeviceInfo(i, info.name, info.defaultSampleRate, info.maxOutputChannels, i == def));
        }
        return list;
    }

    public void Open(int deviceId, int sampleRate, int framesPerBuffer, IAudioCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Close();

        var info = PortAudio.GetDeviceInfo(deviceId);
        var outParams = new StreamParameters
        {
            device = deviceId,
            channelCount = 2,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        _callback = callback;
        _nativeCallback = OnNativeCallback;
        _stream = new PAStream(null, outParams, sampleRate, (uint)framesPerBuffer,
            StreamFlags.ClipOff, _nativeCallback, IntPtr.Zero);

        SampleRate = sampleRate;
        FramesPerBuffer = framesPerBuffer;
        // PortAudioSharp does not expose Pa_GetStreamInfo; report the latency negotiated at open time.
        OutputLatencyMs = info.defaultLowOutputLatency * 1000.0;
    }

    private unsafe StreamCallbackResult OnNativeCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        var span = new Span<float>((void*)output, (int)frameCount * 2);
        _callback!.Render(span, (int)frameCount);
        return StreamCallbackResult.Continue;
    }

    public void Start()
    {
        if (_stream == null) throw new InvalidOperationException("Stream is not open.");
        if (IsRunning) return;
        _stream.Start();
        IsRunning = true;
    }

    public void Stop()
    {
        if (_stream == null || !IsRunning) return;
        _stream.Stop();
        IsRunning = false;
    }

    private void Close()
    {
        if (_stream == null) return;
        try { Stop(); } catch { }
        _stream.Dispose();
        _stream = null;
        _nativeCallback = null;
        _callback = null;
        IsRunning = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        lock (InitLock)
        {
            if (--_initCount == 0) PortAudio.Terminate();
        }
    }
}
