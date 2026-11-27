using System.Runtime.CompilerServices;

namespace Sambient.Dsp.Modulation;

/// <summary>
/// The 24 LFO / MOD waveforms of the original, in display order (same order as the engine's <c>LfoShape</c> enum, so a
/// cast converts between them). The three <c>Low*</c> shapes are the same shapes running at 1/8 of the set rate.
/// </summary>
public enum LfoWave
{
    Sine, Square, Triangle, Saw, ReverseSaw, Random, SmoothRandom, Log, ReverseLog,
    Pulse10, Pulse25, Pulse75, Pulse90, Step2, Step3, Step4, Step5, Step6, Step7,
    RampUp, RampDown, LowSine, LowTriangle, LowSmoothRandom,
}

/// <summary>
/// Allocation-free, deterministic (seeded) LFO. Output is BIPOLAR, -1..+1; destinations that need a unipolar signal map it
/// with <c>u = (x + 1) / 2</c> (see the engine's modulation table).
///
/// Shape definitions over the cycle phase p in [0,1):
///   Sine / Triangle / Saw / ReverseSaw start at 0 (zero-crossing start, Saw/ReverseSaw jump at p = 0.5),
///   Square +1 then -1, Pulse10..90 are +1 for that duty then -1,
///   Random = sample &amp; hold (new value every cycle), SmoothRandom = cosine interpolation between the cycle's random values,
///   Log = fast rise that flattens (-1 -> +1, restarts each cycle), ReverseLog = its time reversal (+1 -> -1),
///   Step2..Step7 = ascending staircase of N levels (-1 -> +1, restarts each cycle),
///   RampUp = linear -1 -> +1 and RampDown = +1 -> -1, both restarting each cycle (with trig 1..8 they act like a one-shot
///   ramp that then holds its end value).
/// Discontinuous shapes are slewed (time constant = min(3 ms, 1/50 of the period)) so a destination never clicks; the slew
/// shrinks with the period so audio-rate use (ring modulation) keeps its edges.
///
/// Shape changes (when <see cref="MorphShapeChanges"/> is set, as the engine does): the output is crossfaded from the value it had
/// at the switch to the new shape's output over ~15 ms, so a destination never steps. A <see cref="Reset"/> cancels the morph.
///
/// Trigger: <see cref="TrigOff"/> (0) = free-running (<see cref="Retrigger"/> does nothing), <see cref="TrigInf"/> (9) = restart
/// the phase on every <see cref="Retrigger"/>, 1..8 = after a retrigger run that many cycles, then HOLD the final value.
/// </summary>
public struct Lfo
{
    public const int TrigOff = 0, TrigInf = 9;
    private const double SlewMaxSeconds = 0.003;

    private float _fs;
    private uint _rng;
    private LfoWave _wave;         // as configured
    private LfoWave _base;         // shape with Low* mapped to the base shape
    private bool _slewed;
    private double _phase, _inc;
    private float _rateHz;
    private int _trig, _cycles;
    private bool _hold;
    private float _a, _b;          // random values of the current / next cycle
    private float _out, _slew;     // slewed output, one-pole coefficient (1 = no slew)
    private float _morphFrom;      // output at the moment of a shape change
    private int _morphLeft, _morphTotal;
    private bool _used;            // Next() has run at least once (the very first Configure never morphs)

    /// <summary>When true a change of shape is crossfaded over ~15 ms instead of stepping (engine use; off for plain DSP use).</summary>
    public bool MorphShapeChanges;

    public Lfo(float sampleRate, uint seed)
    {
        _fs = sampleRate;
        _morphTotal = Math.Max(1, (int)(sampleRate * 0.015f));
        _morphLeft = 0; _morphFrom = 0f; _used = false; MorphShapeChanges = false;
        _rng = seed * 2654435761u + 0x9E3779B9u;
        if (_rng == 0) _rng = 0x1234567u;
        _wave = LfoWave.Sine; _base = LfoWave.Sine;
        _slewed = false; _slew = 1f;
        _phase = 0; _inc = 0; _rateHz = -1f;
        _trig = TrigOff; _cycles = 0; _hold = false;
        _a = Rand(); _b = Rand();
        _out = 0f;
        Configure(LfoWave.Sine, 1f, TrigOff);
        _out = Evaluate(_base, 0f);
    }

    /// <summary>Current (already slewed) output of the last <see cref="Next"/>.</summary>
    public readonly float Value => _morphLeft > 0 ? Blend(_out) : _out;
    public readonly bool IsHolding => _hold;
    public readonly double Phase => _phase;
    public readonly int CompletedCycles => _cycles;

    /// <summary>Sets shape, rate (Hz at the shape's own speed; Low* shapes run at 1/8) and trigger mode. Cheap when unchanged.</summary>
    public void Configure(LfoWave wave, float rateHz, int trig)
    {
        _trig = Math.Clamp(trig, TrigOff, TrigInf);
        if (wave == _wave && rateHz == _rateHz) return;
        if (wave != _wave && MorphShapeChanges && _used) { _morphFrom = Value; _morphLeft = _morphTotal; }
        _wave = wave;
        _base = wave switch
        {
            LfoWave.LowSine => LfoWave.Sine,
            LfoWave.LowTriangle => LfoWave.Triangle,
            LfoWave.LowSmoothRandom => LfoWave.SmoothRandom,
            _ => wave,
        };
        float speed = wave >= LfoWave.LowSine ? 0.125f : 1f;
        if (!(rateHz > 0f)) rateHz = 0f;
        _rateHz = rateHz;
        float eff = rateHz * speed;
        _inc = eff / _fs;
        _slewed = _base is not (LfoWave.Sine or LfoWave.Triangle or LfoWave.SmoothRandom);
        if (_slewed)
        {
            double tau = Math.Clamp(0.02 / Math.Max(eff, 1e-3f), 20e-6, SlewMaxSeconds);
            _slew = (float)(1.0 - Math.Exp(-1.0 / (tau * _fs)));
        }
        else _slew = 1f;
    }

    /// <summary>Note-on: restarts the phase and the cycle counter unless the trigger mode is OFF (free-running).</summary>
    public void Retrigger()
    {
        if (_trig == TrigOff) return;
        Reset();
    }

    /// <summary>Unconditional restart (phase 0, new random values). The output slews to the new start value.</summary>
    public void Reset()
    {
        _phase = 0; _cycles = 0; _hold = false;
        _morphLeft = 0;
        _a = Rand(); _b = Rand();
    }

    /// <summary>Advances one sample and returns the bipolar output.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next()
    {
        if (!_hold)
        {
            _phase += _inc;
            if (_phase >= 1.0) Wrap();
        }
        float target = Evaluate(_base, (float)_phase, _a, _b);
        if (_slewed) _out += (target - _out) * _slew;
        else _out = target;
        _used = true;
        if (_morphLeft > 0)
        {
            float y = Blend(_out);
            _morphLeft--;
            return y;
        }
        return _out;
    }

    /// <summary>Crossfade weight of the new shape = 1 - left/total (a cosine-free linear fade is enough at 15 ms).</summary>
    private readonly float Blend(float v)
    {
        float w = 1f - (float)_morphLeft / _morphTotal;
        return _morphFrom + (v - _morphFrom) * w;
    }

    /// <summary>Fills <paramref name="buffer"/> with consecutive output samples.</summary>
    public void Fill(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++) buffer[i] = Next();
    }

    private void Wrap()
    {
        _phase -= Math.Floor(_phase);
        _cycles++;
        if (_trig >= 1 && _trig <= 8 && _cycles >= _trig)
        {
            _hold = true;
            _phase = 1.0 - 1e-6;      // end of the last cycle: the shape's final value
            return;
        }
        _a = _b; _b = Rand();
    }

    private float Rand()
    {
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return (int)x * (1f / 2147483648f);
    }

    /// <summary>
    /// Deterministic shape value for phase <paramref name="p"/> in [0,1] (Random shapes return 0 here; use the instance).
    /// Low* shapes evaluate like their base shape.
    /// </summary>
    public static float Evaluate(LfoWave wave, float p) => Evaluate(wave, p, 0f, 0f);

    private static float Evaluate(LfoWave wave, float p, float a, float b)
    {
        switch (wave)
        {
            case LfoWave.Sine:
            case LfoWave.LowSine:
                return FastSin(p);
            case LfoWave.Square: return p < 0.5f ? 1f : -1f;
            case LfoWave.Triangle:
            case LfoWave.LowTriangle:
                return p < 0.25f ? 4f * p : p < 0.75f ? 2f - 4f * p : 4f * p - 4f;
            case LfoWave.Saw: { float s = p + 0.5f; s -= MathF.Floor(s); return 2f * s - 1f; }
            case LfoWave.ReverseSaw: { float s = p + 0.5f; s -= MathF.Floor(s); return 1f - 2f * s; }
            case LfoWave.Random: return a;
            case LfoWave.SmoothRandom:
            case LfoWave.LowSmoothRandom:
            {
                float t = 0.5f - 0.5f * FastSin(0.25f + 0.5f * p);   // cosine ease 0 -> 1
                return a + (b - a) * t;
            }
            case LfoWave.Log: return LogCurve(p);
            case LfoWave.ReverseLog: return LogCurve(1f - p);
            case LfoWave.Pulse10: return p < 0.10f ? 1f : -1f;
            case LfoWave.Pulse25: return p < 0.25f ? 1f : -1f;
            case LfoWave.Pulse75: return p < 0.75f ? 1f : -1f;
            case LfoWave.Pulse90: return p < 0.90f ? 1f : -1f;
            case LfoWave.Step2: return Stairs(p, 2);
            case LfoWave.Step3: return Stairs(p, 3);
            case LfoWave.Step4: return Stairs(p, 4);
            case LfoWave.Step5: return Stairs(p, 5);
            case LfoWave.Step6: return Stairs(p, 6);
            case LfoWave.Step7: return Stairs(p, 7);
            case LfoWave.RampUp: return 2f * p - 1f;
            case LfoWave.RampDown: return 1f - 2f * p;
            default: return 0f;
        }
    }

    private const float LogNorm = 1f / (1f - 0.006737947f);     // 1 - e^-5

    private static float LogCurve(float p) => 2f * (1f - MathF.Exp(-5f * Math.Clamp(p, 0f, 1f))) * LogNorm - 1f;

    private static float Stairs(float p, int steps)
    {
        int k = Math.Min((int)(p * steps), steps - 1);
        return 2f * k / (steps - 1) - 1f;
    }

    /// <summary>sin(2 pi p) for any p, 9th-order polynomial (error below 4e-6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FastSin(float p)
    {
        float q = p - MathF.Floor(p + 0.5f);          // [-0.5, 0.5)
        if (q > 0.25f) q = 0.5f - q;
        else if (q < -0.25f) q = -0.5f - q;
        float x = q * (2f * MathF.PI);
        float x2 = x * x;
        return x * (1f + x2 * (-1f / 6f + x2 * (1f / 120f + x2 * (-1f / 5040f + x2 * (1f / 362880f)))));
    }
}
