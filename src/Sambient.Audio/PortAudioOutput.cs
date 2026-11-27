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

    // ---- glitch diagnostics (written by the audio thread, read by the UI) ----
    private long _underflows, _lateGaps, _callbacks, _lastTs, _maxRenderTicks, _maxGapTicks;
    /// <summary>Callbacks where PortAudio reported an output underflow / output priming problem.</summary>
    public long Underflows => Interlocked.Read(ref _underflows);
    /// <summary>Callbacks that arrived more than 1.5 buffer periods after the previous one (audible dropout).</summary>
    public long LateCallbacks => Interlocked.Read(ref _lateGaps);
    public long Callbacks => Interlocked.Read(ref _callbacks);
    /// <summary>Longest time spent inside Render, in ms.</summary>
    public double MaxRenderMs => Interlocked.Read(ref _maxRenderTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    /// <summary>Longest gap between two callbacks, in ms.</summary>
    public double MaxCallbackGapMs => Interlocked.Read(ref _maxGapTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _underflows, 0); Interlocked.Exchange(ref _lateGaps, 0);
        Interlocked.Exchange(ref _maxRenderTicks, 0); Interlocked.Exchange(ref _maxGapTicks, 0);
    }

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
            // at least ~3 buffers of headroom: a short stall of the managed audio thread (GC, CPU contention) must not
            // underrun the device buffer (audible click)
            suggestedLatency = Math.Max(info.defaultLowOutputLatency, 3.0 * framesPerBuffer / sampleRate),
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        _callback = callback;
        _nativeCallback = OnNativeCallback;
        _stream = new PAStream(null, outParams, sampleRate, (uint)framesPerBuffer,
            StreamFlags.ClipOff, _nativeCallback, IntPtr.Zero);

        SampleRate = sampleRate;
        FramesPerBuffer = framesPerBuffer;
        // PortAudioSharp does not expose Pa_GetStreamInfo; report the latency negotiated at open time.
        OutputLatencyMs = Math.Max(info.defaultLowOutputLatency, 3.0 * framesPerBuffer / sampleRate) * 1000.0;
    }

    private unsafe StreamCallbackResult OnNativeCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((statusFlags & (StreamCallbackFlags.OutputUnderflow | StreamCallbackFlags.PrimingOutput)) != 0)
            Interlocked.Increment(ref _underflows);
        if (_lastTs != 0 && SampleRate > 0)
        {
            long gap = t0 - _lastTs;
            if (gap > Interlocked.Read(ref _maxGapTicks)) Interlocked.Exchange(ref _maxGapTicks, gap);
            double period = (double)frameCount / SampleRate * System.Diagnostics.Stopwatch.Frequency;
            if (gap > period * 1.5) Interlocked.Increment(ref _lateGaps);
        }
        _lastTs = t0;
        Interlocked.Increment(ref _callbacks);

        var span = new Span<float>((void*)output, (int)frameCount * 2);
        _callback!.Render(span, (int)frameCount);

        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (dt > Interlocked.Read(ref _maxRenderTicks)) Interlocked.Exchange(ref _maxRenderTicks, dt);
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
