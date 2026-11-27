using System.Runtime.CompilerServices;

namespace Sambient.Dsp.Filters;

public enum FilterMode { Off, LowPass, HighPass, BandPass }

/// <summary>
/// Cytomic (Andrew Simper) TPT zero-delay-feedback state variable filter, 12 dB/oct.
/// Stable under per-sample modulation. BP output is normalised to unity gain at centre.
/// Use one instance per channel.
/// </summary>
public struct StateVariableFilter
{
    private float _ic1, _ic2;
    private float _a1, _a2, _a3, _k;
    private readonly float _sampleRate;
    private float _lastCutoff, _lastQ;

    public FilterMode Mode;

    public StateVariableFilter(float sampleRate, FilterMode mode = FilterMode.LowPass)
    {
        _sampleRate = sampleRate;
        Mode = mode;
        _ic1 = _ic2 = 0;
        _lastCutoff = _lastQ = -1;
        SetParameters(1000f, 0.707f);
    }

    /// <summary>Q equivalent for a band-pass of the given bandwidth in octaves (between -3 dB points).</summary>
    public static float OctavesToQ(float octaves)
    {
        float n = MathF.Pow(2f, Math.Max(octaves, 0.01f));
        return MathF.Sqrt(n) / (n - 1f);
    }

    /// <summary>Sets cutoff (clamped to 20 Hz .. 0.45 fs) and Q (min 0.05). Cheap; no-op if unchanged.</summary>
    public void SetParameters(float cutoffHz, float q)
    {
        if (cutoffHz == _lastCutoff && q == _lastQ) return;
        _lastCutoff = cutoffHz; _lastQ = q;
        float fc = Math.Clamp(cutoffHz, 20f, 0.45f * _sampleRate);
        float g = MathF.Tan(MathF.PI * fc / _sampleRate);
        _k = 1f / Math.Max(q, 0.05f);
        _a1 = 1f / (1f + g * (g + _k));
        _a2 = g * _a1;
        _a3 = g * _a2;
    }

    public void SetBandwidthOctaves(float cutoffHz, float octaves) => SetParameters(cutoffHz, OctavesToQ(octaves));

    public void Reset() => _ic1 = _ic2 = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        if (Mode == FilterMode.Off) return x;
        float v3 = x - _ic2;
        float v1 = _a1 * _ic1 + _a2 * v3;
        float v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
        _ic1 = 2f * v1 - _ic1;
        _ic2 = 2f * v2 - _ic2;
        // Denormal protection: flush tiny state to zero.
        if (MathF.Abs(_ic1) < 1e-18f) _ic1 = 0f;
        if (MathF.Abs(_ic2) < 1e-18f) _ic2 = 0f;
        return Mode switch
        {
            FilterMode.LowPass => v2,
            FilterMode.HighPass => x - _k * v1 - v2,
            _ => _k * v1,
        };
    }

    public void Process(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++) buffer[i] = Process(buffer[i]);
    }
}
