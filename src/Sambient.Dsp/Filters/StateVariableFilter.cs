using System.Runtime.CompilerServices;

namespace Sambient.Dsp.Filters;

public enum FilterMode { Off, LowPass, HighPass, BandPass }

/// <summary>
/// Cytomic (Andrew Simper) TPT zero-delay-feedback state variable filter, 12 dB/oct.
/// Stable under per-sample modulation. BP output is normalised to unity gain at centre.
/// Use one instance per channel.
/// <para>
/// Changing <see cref="Mode"/> crossfades the old and the new tap over ~15 ms (the state keeps running while a fade is
/// in progress) so that switching LP / HP / BP / OFF never steps the output. Coming from OFF the (stale) state is cleared first.
/// </para>
/// </summary>
public struct StateVariableFilter
{
    private float _ic1, _ic2;
    private float _a1, _a2, _a3, _k;
    private readonly float _sampleRate;
    private float _lastCutoff, _lastQ;
    private FilterMode _mode, _prev;
    private int _fade, _fadeTotal;

    /// <summary>Filter type. Changing it starts a ~15 ms crossfade from the previous type.</summary>
    public FilterMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            _prev = _mode; _mode = value;
            if (_prev == FilterMode.Off) _ic1 = _ic2 = 0f;      // idle state is stale: start the filter from rest
            _fade = _fadeTotal;
        }
    }

    public StateVariableFilter(float sampleRate, FilterMode mode = FilterMode.LowPass)
    {
        _sampleRate = sampleRate;
        _mode = mode; _prev = mode; _fade = 0;
        _fadeTotal = Math.Max(1, (int)(sampleRate * 0.015f));
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

    /// <summary>Sets the type without a crossfade (a new note starting: nothing to fade from).</summary>
    public void SetModeImmediate(FilterMode mode) { _mode = _prev = mode; _fade = 0; }

    public void SetBandwidthOctaves(float cutoffHz, float octaves) => SetParameters(cutoffHz, OctavesToQ(octaves));

    public void Reset() => _ic1 = _ic2 = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        if (_mode == FilterMode.Off && _fade == 0) return x;
        float v3 = x - _ic2;
        float v1 = _a1 * _ic1 + _a2 * v3;
        float v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
        _ic1 = 2f * v1 - _ic1;
        _ic2 = 2f * v2 - _ic2;
        // Denormal protection: flush tiny state to zero.
        if (MathF.Abs(_ic1) < 1e-18f) _ic1 = 0f;
        if (MathF.Abs(_ic2) < 1e-18f) _ic2 = 0f;
        float y = Tap(_mode, x, v1, v2);
        if (_fade > 0)
        {
            float w = (float)_fade / _fadeTotal;         // 1 -> 0: weight of the previous type
            y += (Tap(_prev, x, v1, v2) - y) * w;
            _fade--;
        }
        return y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly float Tap(FilterMode mode, float x, float v1, float v2) => mode switch
    {
        FilterMode.LowPass => v2,
        FilterMode.HighPass => x - _k * v1 - v2,
        FilterMode.BandPass => _k * v1,
        _ => x,
    };

    public void Process(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++) buffer[i] = Process(buffer[i]);
    }
}
