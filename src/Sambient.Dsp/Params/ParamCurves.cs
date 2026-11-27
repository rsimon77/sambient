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

    // ---------------------------------------------------------------- modulation / structure curves (M3)

    /// <summary>
    /// LFO rate in Hz, exponential: knob 0 -> 0.02 Hz, 127 -> 30 Hz (64 ~ 0.8 Hz, 32 ~ 0.13 Hz, 96 ~ 5 Hz).
    /// The Low* shapes run at 1/8 of this.
    /// </summary>
    public static float LfoRateHz(float v) => 0.02f * MathF.Pow(1500f, Norm(v));

    /// <summary>
    /// MOD LFO rate in Hz. Same as <see cref="LfoRateHz"/> except for ATMOS1 (<paramref name="ringMod"/>), where the top of the
    /// range reaches audio rate for metallic ring modulation: 0.02 Hz .. 2 kHz, exponential (64 ~ 6 Hz, 96 ~ 90 Hz, 127 = 2 kHz).
    /// </summary>
    public static float ModRateHz(float v, bool ringMod = false)
        => ringMod ? 0.02f * MathF.Pow(100000f, Norm(v)) : LfoRateHz(v);

    /// <summary>PITCH knob (-240..+240) to semitones: 1/20 semitone (5 cent) steps, +-12 semitones.</summary>
    public static float PitchSemitones(float units) => Math.Clamp(units, -240f, 240f) * (1f / 20f);

    /// <summary>
    /// Detune knob (0..127, centre 63.5 = 0) to cents. Signed square law: +-50 cents at the ends, +-12.5 cents at a
    /// quarter turn, 0.5 cent at 10 % of the travel, so the slow "fluctuation" beating of drones is finely adjustable.
    /// </summary>
    public static float DetuneCents(float v)
    {
        float x = (Math.Clamp(v, 0f, MaxKnob) - 63.5f) * (1f / 63.5f);
        return 50f * x * MathF.Abs(x);
    }

    /// <summary>Equal-power balance (0..127, centre 63.5): (gain of osc A, gain of osc B). Ends isolate one oscillator.</summary>
    public static (float a, float b) BalanceGains(float v)
    {
        float x = (Math.Clamp(v, 0f, MaxKnob) - 63.5f) * (1f / 63.5f);          // -1..1
        float t = (x + 1f) * (MathF.PI * 0.25f);
        return (MathF.Cos(t), MathF.Sin(t));
    }

    /// <summary>
    /// ATMOS2 modulator ratio from PITCH (-240..+240): 0.50 at -240, exactly 1.00 at 0, 15.99 at +240 (exponential on each side).
    /// </summary>
    public static float Atmos2Ratio(float pitch)
    {
        pitch = Math.Clamp(pitch, -240f, 240f);
        return pitch <= 0f
            ? MathF.Pow(2f, pitch * (1f / 240f))                      // 0.5 .. 1
            : MathF.Pow(15.99f, pitch * (1f / 240f));                 // 1 .. 15.99
    }

    /// <summary>Inverse of <see cref="Atmos2Ratio"/> (for the UI): ratio 0.50..15.99 -> PITCH value, rounded to an integer.</summary>
    public static int Atmos2RatioToPitch(float ratio)
    {
        ratio = Math.Clamp(ratio, 0.5f, 15.99f);
        float p = ratio <= 1f ? 240f * MathF.Log2(ratio) : 240f * MathF.Log(ratio) / MathF.Log(15.99f);
        return (int)MathF.Round(p);
    }

    /// <summary>ATMOS2 FM index (peak phase deviation in radians) from the FM amount knob: 0 .. 10, square law.</summary>
    public static float Atmos2FmIndex(float v) { float t = Norm(v); return 10f * t * t; }

    /// <summary>ATMOS2 modulator self-feedback (radians) from the feedback knob: 0 .. 1.6, t^1.5 law.</summary>
    public static float Atmos2Feedback(float v) => 1.6f * MathF.Pow(Norm(v), 1.5f);
}
