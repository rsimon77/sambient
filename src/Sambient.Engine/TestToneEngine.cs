using Sambient.Dsp;

namespace Sambient.Engine;

/// <summary>
/// Minimal test-tone generator. Parameters are written from the UI thread and read once per block
/// on the audio thread. The render path performs no allocation, locking or LINQ.
/// </summary>
public sealed class TestToneEngine
{
    private readonly double _sampleRate;
    private SineOscillator _osc;
    private float _currentGain;   // audio thread only (linear)

    private float _frequency = 220f;
    private float _gainDb = -18f;
    private int _enabled;

    public TestToneEngine(double sampleRate)
    {
        _sampleRate = sampleRate;
        _osc = new SineOscillator(220.0, sampleRate);
    }

    /// <summary>Frequency in Hz (UI thread).</summary>
    public float Frequency
    {
        get => Volatile.Read(ref _frequency);
        set => Volatile.Write(ref _frequency, value);
    }

    /// <summary>Gain in dB (UI thread).</summary>
    public float GainDb
    {
        get => Volatile.Read(ref _gainDb);
        set => Volatile.Write(ref _gainDb, value);
    }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    /// <summary>Renders <paramref name="frames"/> stereo interleaved frames into <paramref name="interleavedStereo"/>.</summary>
    public void Render(Span<float> interleavedStereo, int frames)
    {
        float freq = Volatile.Read(ref _frequency);
        float db = Volatile.Read(ref _gainDb);
        bool enabled = Volatile.Read(ref _enabled) != 0;

        float target = enabled ? MathF.Pow(10f, db / 20f) : 0f;
        float start = _currentGain;

        if (start == 0f && target == 0f)
        {
            interleavedStereo.Slice(0, frames * 2).Clear();
            return;
        }

        _osc.SetFrequency(freq, _sampleRate);
        float step = (target - start) / frames;
        float g = start;
        for (int i = 0; i < frames; i++)
        {
            g += step;
            float s = _osc.Next() * g;
            interleavedStereo[2 * i] = s;
            interleavedStereo[2 * i + 1] = s;
        }
        _currentGain = target;
    }
}
