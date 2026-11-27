namespace Sambient.Dsp.Effects;

/// <summary>The six master effects of the original device.</summary>
public enum MasterFxType { Off, TapeDelay, ReverseDelay, Overdrive, Crush, TiltEq, Chorus }

/// <summary>
/// Master insert effect with two macro controls (FX SPEED, FX AMOUNT).
/// Real-time safe; audio thread only. Off = bit-exact passthrough; switching types crossfades
/// (about 20 ms) between the outgoing and the freshly reset incoming effect.
/// </summary>
public sealed class MasterFx
{
    private const int MaxChunk = 4096;

    private readonly FxBase[] _fx;
    private readonly float[] _tmpL = new float[MaxChunk], _tmpR = new float[MaxChunk];
    private readonly int _fadeTotal;
    private int _fadeLeft;
    private MasterFxType _old = MasterFxType.Off;
    private float _speed = 64f, _amount = 0f;
    private double _bpm = 120.0;

    public MasterFx(float sampleRate)
    {
        SampleRate = sampleRate;
        _fx = new FxBase[]
        {
            null!,
            new TapeDelayFx(sampleRate),
            new ReverseDelayFx(sampleRate),
            new OverdriveFx(sampleRate),
            new CrushFx(sampleRate),
            new TiltEqFx(sampleRate),
            new ChorusFx(sampleRate),
        };
        _fadeTotal = Math.Max(16, (int)(0.02f * sampleRate));
        for (int i = 1; i < _fx.Length; i++) { _fx[i].SetSpeed(_speed / 127f); _fx[i].SetAmount(_amount / 127f); _fx[i].Reset(); }
    }

    public float SampleRate { get; }
    public MasterFxType Type { get; private set; } = MasterFxType.Off;

    public void SetType(MasterFxType type)
    {
        if (type == Type) return;
        // If a crossfade is still running, the older effect is simply dropped (a 20 ms transient at worst).
        _old = Type;
        Type = type;
        if (type != MasterFxType.Off)
        {
            var f = _fx[(int)type];
            f.SetSpeed(_speed / 127f);
            f.SetAmount(_amount / 127f);
            f.Reset();
            f.StartFadeIn();
        }
        _fadeLeft = _fadeTotal;
    }

    /// <summary>FX SPEED knob 0..127 (delay time / gain / bit+rate / tilt frequency / chorus rate).</summary>
    public void SetSpeed(float speed0To127)
    {
        _speed = speed0To127;
        if (Type != MasterFxType.Off) _fx[(int)Type].SetSpeed(FxMath.Clamp01(speed0To127 / 127f));
    }

    /// <summary>FX AMOUNT knob 0..127 (feedback+mix / mix / balance / depth+mix).</summary>
    public void SetAmount(float amount0To127)
    {
        _amount = amount0To127;
        if (Type != MasterFxType.Off) _fx[(int)Type].SetAmount(FxMath.Clamp01(amount0To127 / 127f));
    }

    /// <summary>Current tempo; stored for later musically quantized delay times.</summary>
    public void SetTempo(double bpm) => _bpm = bpm;

    public void Reset()
    {
        for (int i = 1; i < _fx.Length; i++) _fx[i].Reset();
        _fadeLeft = 0;
        _old = MasterFxType.Off;
    }

    /// <summary>Processes a stereo block in place. Off = bit-exact passthrough.</summary>
    public void Process(Span<float> left, Span<float> right)
    {
        if (Type == MasterFxType.Off && _fadeLeft == 0) return;
        int total = Math.Min(left.Length, right.Length);
        for (int off = 0; off < total; off += MaxChunk)
        {
            int n = Math.Min(MaxChunk, total - off);
            var l = left.Slice(off, n);
            var r = right.Slice(off, n);
            if (_fadeLeft == 0)
            {
                _fx[(int)Type].Process(l, r);
                continue;
            }

            // Crossfade: the old effect runs on a copy of the dry block, the new one in place.
            var tl = _tmpL.AsSpan(0, n);
            var tr = _tmpR.AsSpan(0, n);
            l.CopyTo(tl); r.CopyTo(tr);
            if (_old != MasterFxType.Off) _fx[(int)_old].Process(tl, tr);
            if (Type != MasterFxType.Off) _fx[(int)Type].Process(l, r);
            for (int i = 0; i < n; i++)
            {
                float w = _fadeLeft > 0 ? 1f - (float)_fadeLeft / _fadeTotal : 1f;
                l[i] = tl[i] + (l[i] - tl[i]) * w;
                r[i] = tr[i] + (r[i] - tr[i]) * w;
                if (_fadeLeft > 0) _fadeLeft--;
            }
            // For Off -> old effect the "new" signal is the dry block itself (l is untouched by Off), as intended.
        }
    }
}
