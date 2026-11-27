using System.Runtime.CompilerServices;

namespace Sambient.Dsp.Effects;

/// <summary>Small shared DSP helpers for the effects (all allocation-free).</summary>
internal static class FxMath
{
    public const float TwoPi = 6.2831855f;

    /// <summary>Rational tanh approximation, exactly +-1 beyond |x| = 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tanh(float x)
    {
        if (x > 3f) return 1f;
        if (x < -3f) return -1f;
        float x2 = x * x;
        return x * (27f + x2) / (27f + 9f * x2);
    }

    /// <summary>One-pole lowpass coefficient for y += a (x - y).</summary>
    public static float OnePoleCoef(float fc, float fs)
    {
        fc = Math.Min(fc, 0.45f * fs);
        return 1f - MathF.Exp(-TwoPi * fc / fs);
    }

    /// <summary>Per-sample smoothing coefficient for a time constant in seconds.</summary>
    public static float SmoothCoef(float seconds, float fs) => 1f - MathF.Exp(-1f / (seconds * fs));

    public static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Hermite(float xm1, float x0, float x1, float x2, float f)
    {
        float c1 = 0.5f * (x1 - xm1);
        float c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * f + c2) * f + c1) * f + x0;
    }

    /// <summary>
    /// Reads a power-of-two circular buffer <paramref name="delay"/> samples behind the write index
    /// <paramref name="w"/> (the slot just written has delay 0). Requires delay &gt;= 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReadCubic(float[] buf, int mask, int w, float delay)
    {
        int di = (int)delay;
        float f = delay - di;
        int i = w - di;
        return Hermite(buf[(i + 1) & mask], buf[i & mask], buf[(i - 1) & mask], buf[(i - 2) & mask], f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReadLinear(float[] buf, int mask, int w, float delay)
    {
        int di = (int)delay;
        float f = delay - di;
        int i = w - di;
        float a = buf[i & mask], b = buf[(i - 1) & mask];
        return a + (b - a) * f;
    }

    public static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
}

/// <summary>Direct-form-II-transposed biquad.</summary>
internal struct Biquad
{
    public float B0, B1, B2, A1, A2, Z1, Z2;

    public void SetIdentity() { B0 = 1f; B1 = B2 = A1 = A2 = 0f; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = B0 * x + Z1;
        Z1 = B1 * x - A1 * y + Z2;
        Z2 = B2 * x - A2 * y;
        return y;
    }

    public void Clear() { Z1 = Z2 = 0f; }

    public void SetShelf(bool high, float freq, float gainDb, float fs)
    {
        float A = MathF.Pow(10f, gainDb / 40f);
        float w0 = FxMath.TwoPi * Math.Min(freq, 0.45f * fs) / fs;
        float cs = MathF.Cos(w0), sn = MathF.Sin(w0);
        const float S = 1f;
        float alpha = sn * 0.5f * MathF.Sqrt((A + 1f / A) * (1f / S - 1f) + 2f);
        float tsa = 2f * MathF.Sqrt(A) * alpha;
        float b0, b1, b2, a0, a1, a2;
        if (!high)
        {
            b0 = A * ((A + 1f) - (A - 1f) * cs + tsa);
            b1 = 2f * A * ((A - 1f) - (A + 1f) * cs);
            b2 = A * ((A + 1f) - (A - 1f) * cs - tsa);
            a0 = (A + 1f) + (A - 1f) * cs + tsa;
            a1 = -2f * ((A - 1f) + (A + 1f) * cs);
            a2 = (A + 1f) + (A - 1f) * cs - tsa;
        }
        else
        {
            b0 = A * ((A + 1f) + (A - 1f) * cs + tsa);
            b1 = -2f * A * ((A - 1f) + (A + 1f) * cs);
            b2 = A * ((A + 1f) + (A - 1f) * cs - tsa);
            a0 = (A + 1f) - (A - 1f) * cs + tsa;
            a1 = 2f * ((A - 1f) - (A + 1f) * cs);
            a2 = (A + 1f) - (A - 1f) * cs - tsa;
        }
        float inv = 1f / a0;
        B0 = b0 * inv; B1 = b1 * inv; B2 = b2 * inv; A1 = a1 * inv; A2 = a2 * inv;
    }
}

/// <summary>Schroeder allpass section (g + z^-d) / (1 + g z^-d).</summary>
internal sealed class AllpassStage
{
    private readonly float[] _buf;
    private int _idx;
    private readonly float _g;

    public AllpassStage(int length, float g) { _buf = new float[Math.Max(1, length)]; _g = g; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float v = _buf[_idx];
        float w = x - _g * v;
        _buf[_idx] = w;
        if (++_idx == _buf.Length) _idx = 0;
        return v + _g * w;
    }

    public void Clear() { Array.Clear(_buf); _idx = 0; }
}

/// <summary>
/// Dual-grain crossfading delay-line pitch shifter, +12 semitones (ratio 2). Hann-windowed grains,
/// half a grain apart, so the windows sum to exactly one.
/// </summary>
internal sealed class OctaveUpShifter
{
    private readonly float[] _buf;
    private readonly int _mask;
    private readonly float _grain;
    private int _w;
    private float _phase;

    public OctaveUpShifter(float sampleRate, float grainSeconds = 0.075f)
    {
        _grain = MathF.Round(grainSeconds * sampleRate);
        _buf = new float[FxMath.NextPow2((int)_grain + 16)];
        _mask = _buf.Length - 1;
    }

    public void Clear() { Array.Clear(_buf); _phase = 0f; _w = 0; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        _buf[_w & _mask] = x;
        float p1 = _phase, p2 = _phase + 0.5f;
        if (p2 >= 1f) p2 -= 1f;
        float s1 = MathF.Sin(MathF.PI * p1), s2 = MathF.Sin(MathF.PI * p2);
        float y = s1 * s1 * FxMath.ReadCubic(_buf, _mask, _w, 2f + _grain * p1)
                + s2 * s2 * FxMath.ReadCubic(_buf, _mask, _w, 2f + _grain * p2);
        _w++;
        _phase -= 1f / _grain; // read pointer advances 2 samples per sample -> delay shrinks 1 sample per sample
        if (_phase < 0f) _phase += 1f;
        return y;
    }
}
