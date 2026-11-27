using System.Runtime.CompilerServices;

namespace Sambient.Dsp.Params;

/// <summary>
/// Hardware-style 0..127 knob values mapped to physical units. Pure functions, allocation-free.
/// Input values are clamped to the documented range.
/// </summary>
public static class ParamCurves
{
    public const float MaxKnob = 127f;

    // Time curve: ms = Max * (exp(K*t) - 1) / (exp(K) - 1), t = v/127.  K = 5 gives
    // v=64 -> ~775 ms (10 s range) / ~1550 ms (20 s range); fine resolution at short times.
    private const float TimeK = 5f;
    private static readonly float TimeNorm = 1f / (MathF.Exp(TimeK) - 1f);

    private const float CutoffMinHz = 70f, CutoffMaxHz = 14400f;
    private const float QMin = 0.3f, QMax = 10f;
    private const float BpOctWide = 3.3f, BpOctNarrow = 0.5f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Norm(float v) => Math.Clamp(v, 0f, MaxKnob) * (1f / MaxKnob);

    private static float TimeMs(float v, float maxMs)
    {
        float t = Norm(v);
        return maxMs * (MathF.Exp(TimeK * t) - 1f) * TimeNorm;
    }

    /// <summary>Attack time, 0 -> 0 ms, 127 -> 10 000 ms (exponential; 64 ~ 775 ms).</summary>
    public static float AttackMs(float v) => TimeMs(v, 10000f);

    /// <summary>Release time, 127 -> 10 000 ms, or 20 000 ms when <paramref name="longRange"/> (DRONE structures).</summary>
    public static float ReleaseMs(float v, bool longRange = false) => TimeMs(v, longRange ? 20000f : 10000f);

    /// <summary>Cutoff 70 Hz .. 14 400 Hz, exponential (64 ~ 1 kHz).</summary>
    public static float CutoffHz(float v) => CutoffMinHz * MathF.Pow(CutoffMaxHz / CutoffMinHz, Norm(v));

    /// <summary>Inverse of <see cref="CutoffHz"/>: Hz -> 0..127 (clamped).</summary>
    public static float CutoffToKnob(float hz)
    {
        hz = Math.Clamp(hz, CutoffMinHz, CutoffMaxHz);
        return MaxKnob * MathF.Log(hz / CutoffMinHz) / MathF.Log(CutoffMaxHz / CutoffMinHz);
    }

    /// <summary>Resonance Q 0.3 .. 10, exponential.</summary>
    public static float ResonanceQ(float v) => QMin * MathF.Pow(QMax / QMin, Norm(v));

    /// <summary>
    /// Band-pass bandwidth in octaves, used instead of Q in BP mode. Higher knob = NARROWER
    /// (resonance-like feel): 0 -> 3.3 oct, 127 -> 0.5 oct, exponential.
    /// </summary>
    public static float BandpassOctaves(float v) => BpOctWide * MathF.Pow(BpOctNarrow / BpOctWide, Norm(v));

    /// <summary>
    /// Level gain (linear). 0 -> silence, 63.5 -> 0 dB, 127 -> +6 dB. Between 0 and 63.5 the
    /// curve is dB-linear from -60 dB; below knob value 1 it additionally fades to true zero.
    /// </summary>
    public static float LevelGain(float v)
    {
        v = Math.Clamp(v, 0f, MaxKnob);
        if (v <= 0f) return 0f;
        float db = v <= 63.5f ? -60f * (1f - v / 63.5f) : 6f * (v - 63.5f) / 63.5f;
        float g = MathF.Pow(10f, db * 0.05f);
        return v < 1f ? g * v : g;
    }

    /// <summary>Constant-power pan law for pan in -63..+63 (-63 hard left, 0 centre, +63 hard right).</summary>
    public static (float l, float r) PanGains(float pan)
    {
        pan = Math.Clamp(pan, -63f, 63f);
        float a = (pan + 63f) * (1f / 126f) * (MathF.PI * 0.5f);
        return (MathF.Cos(a), MathF.Sin(a));
    }

    /// <summary>Glide time 0 .. 10 000 ms (same exponential shape as attack).</summary>
    public static float GlideMs(float v) => TimeMs(v, 10000f);

    /// <summary>Unison detune 0 .. 20 cents, linear.</summary>
    public static float UnisonDetuneCents(float v) => 20f * Norm(v);

    /// <summary>Effect send gain, linear: 0 -> 0, 127 -> 1.0.</summary>
    public static float SendGain(float v) => Norm(v);
}
