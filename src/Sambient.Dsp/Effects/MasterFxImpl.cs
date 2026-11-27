namespace Sambient.Dsp.Effects;

/// <summary>Base class of the individual master effects. Parameters arrive normalised 0..1.</summary>
internal abstract class FxBase
{
    protected readonly float Fs;
    protected readonly float Smooth;
    protected float Speed = 0.5f, Amount;
    protected bool Snap = true;
    private float _inGain = 1f;
    private readonly float _inStep;

    /// <summary>
    /// Starts a ~15 ms fade-in of the signal that delay-line effects write into their (just cleared) buffers, so the first echo /
    /// chorus tap never starts with a step. Called by <see cref="MasterFx"/> when it switches to this effect.
    /// </summary>
    public void StartFadeIn() => _inGain = 0f;

    /// <summary>Per-sample input gain for the delay-line writes (1 unless <see cref="StartFadeIn"/> is running).</summary>
    protected float NextInGain()
    {
        if (_inGain >= 1f) return 1f;
        _inGain += _inStep;
        return _inGain > 1f ? 1f : _inGain;
    }

    protected FxBase(float fs) { Fs = fs; Smooth = FxMath.SmoothCoef(0.025f, fs); _inStep = 1f / Math.Max(1f, 0.015f * fs); }

    public void SetSpeed(float s01) { Speed = s01; OnParams(); }
    public void SetAmount(float a01) { Amount = a01; OnParams(); }
    public void Reset() { Clear(); Snap = true; OnParams(); }

    protected abstract void OnParams();
    protected abstract void Clear();
    public abstract void Process(Span<float> l, Span<float> r);
}

/// <summary>Tape delay: exponential time 30 ms..2 s with tape-style slewing, wow/flutter, saturated + darkened ping-pong feedback.</summary>
internal sealed class TapeDelayFx : FxBase
{
    private readonly float[] _bl, _br;
    private readonly int _mask;
    private int _w;
    private float _timeT, _timeS, _timeCoef;
    private float _mixT, _mixS, _fbT, _fbS;
    private float _wowPh, _fltPh, _wowW, _fltW;
    private float _lpL, _lpR, _hpL, _hpR, _lpA, _hpA;
    private float _flip = 1e-14f;

    public TapeDelayFx(float fs) : base(fs)
    {
        int len = FxMath.NextPow2((int)(2.2f * fs) + 64);
        _bl = new float[len]; _br = new float[len]; _mask = len - 1;
        _timeCoef = FxMath.SmoothCoef(0.3f, fs); // slow slew: moving the knob bends the pitch like a tape machine
        _wowW = FxMath.TwoPi * 0.47f / fs;
        _fltW = FxMath.TwoPi * 5.3f / fs;
        _lpA = FxMath.OnePoleCoef(3800f, fs);
        _hpA = FxMath.OnePoleCoef(90f, fs);
    }

    protected override void OnParams()
    {
        _timeT = 0.03f * MathF.Pow(2f / 0.03f, Speed) * Fs;
        float a = Amount;
        _fbT = a <= 0.504f ? a * 0.992f : 0.5f + (a - 0.504f) / 0.496f * 0.4f;
        _mixT = Math.Min(1f, a * 2.5f) * 0.85f;
        if (Snap) { _timeS = _timeT; _mixS = _mixT; _fbS = _fbT; }
    }

    protected override void Clear()
    {
        Array.Clear(_bl); Array.Clear(_br);
        _lpL = _lpR = _hpL = _hpR = 0f; _w = 0; _wowPh = _fltPh = 0f;
    }

    public override void Process(Span<float> l, Span<float> r)
    {
        if (Snap) { _timeS = _timeT; _mixS = _mixT; _fbS = _fbT; Snap = false; }
        float sm = Smooth, tc = _timeCoef;
        float maxD = _bl.Length - 8f;
        for (int i = 0; i < l.Length; i++)
        {
            _timeS += (_timeT - _timeS) * tc;
            _mixS += (_mixT - _mixS) * sm;
            _fbS += (_fbT - _fbS) * sm;
            _wowPh += _wowW; if (_wowPh > FxMath.TwoPi) _wowPh -= FxMath.TwoPi;
            _fltPh += _fltW; if (_fltPh > FxMath.TwoPi) _fltPh -= FxMath.TwoPi;
            float depthW = 0.0011f * _timeS + 0.00004f * Fs, depthF = 0.00003f * Fs;
            float modL = depthW * MathF.Sin(_wowPh) + depthF * MathF.Sin(_fltPh);
            float modR = depthW * MathF.Sin(_wowPh + 1.7f) + depthF * MathF.Sin(_fltPh + 2.3f);
            float dL = Math.Clamp(_timeS + modL, 4f, maxD), dR = Math.Clamp(_timeS + modR, 4f, maxD);
            float tapL = FxMath.ReadCubic(_bl, _mask, _w - 1, dL - 1f);
            float tapR = FxMath.ReadCubic(_br, _mask, _w - 1, dR - 1f);

            // feedback path: tape saturation, head-bump high-pass, darkening low-pass
            float fL = FxMath.Tanh(tapR * 1.4f) * (1f / 1.4f), fR = FxMath.Tanh(tapL * 1.4f) * (1f / 1.4f);
            _lpL += _lpA * (fL - _lpL); _lpR += _lpA * (fR - _lpR);
            _hpL += _hpA * (_lpL - _hpL); _hpR += _hpA * (_lpR - _hpR);
            fL = _lpL - _hpL; fR = _lpR - _hpR;

            float inL = l[i], inR = r[i];
            _flip = -_flip;
            float ig = NextInGain();
            float m = 0.5f * (inL + inR) * ig + _flip;
            _bl[_w & _mask] = m + _fbS * fL;      // input enters the left line, feedback ping-pongs L -> R -> L
            _br[_w & _mask] = _fbS * fR + _flip;
            _w++;
            l[i] = inL + _mixS * tapL;
            r[i] = inR + _mixS * tapR;
        }
    }
}

/// <summary>Reverse delay: two overlapping Hann-windowed grains read the buffer backwards; amount = mix + feedback.</summary>
internal sealed class ReverseDelayFx : FxBase
{
    private readonly float[] _bl, _br;
    private readonly int _mask;
    private int _w;
    private float _lenT, _lenS, _lenCoef, _phase;
    private float _mixT, _mixS, _fbT, _fbS, _lpA, _lpL, _lpR, _flip = 1e-14f;

    public ReverseDelayFx(float fs) : base(fs)
    {
        int len = FxMath.NextPow2((int)(3.1f * fs) + 64);
        _bl = new float[len]; _br = new float[len]; _mask = len - 1;
        _lenCoef = FxMath.SmoothCoef(0.25f, fs);
        _lpA = FxMath.OnePoleCoef(5500f, fs);
    }

    protected override void OnParams()
    {
        _lenT = 0.08f * MathF.Pow(1.5f / 0.08f, Speed) * Fs;
        _mixT = Math.Min(1f, Amount * 2.2f) * 0.9f;
        _fbT = Amount * 0.62f;
        if (Snap) { _lenS = _lenT; _mixS = _mixT; _fbS = _fbT; }
    }

    protected override void Clear() { Array.Clear(_bl); Array.Clear(_br); _w = 0; _phase = 0f; _lpL = _lpR = 0f; }

    public override void Process(Span<float> l, Span<float> r)
    {
        if (Snap) { _lenS = _lenT; _mixS = _mixT; _fbS = _fbT; Snap = false; }
        float sm = Smooth;
        for (int i = 0; i < l.Length; i++)
        {
            _lenS += (_lenT - _lenS) * _lenCoef;
            _mixS += (_mixT - _mixS) * sm;
            _fbS += (_fbT - _fbS) * sm;
            _phase += 1f / _lenS; if (_phase >= 1f) _phase -= 1f;
            // Each grain's delay grows by 2 samples per sample -> playback runs backwards at normal speed.
            float wetL = Grain(_bl, _phase) + Grain(_bl, Wrap(_phase + 0.5f));
            float wetR = Grain(_br, Wrap(_phase + 0.25f)) + Grain(_br, Wrap(_phase + 0.75f));
            wetL = Soft(wetL); wetR = Soft(wetR);

            _lpL += _lpA * (wetL - _lpL); _lpR += _lpA * (wetR - _lpR);
            _flip = -_flip;
            float inL = l[i], inR = r[i];
            float ig = NextInGain();
            _bl[_w & _mask] = inL * ig + _fbS * _lpL + _flip;
            _br[_w & _mask] = inR * ig + _fbS * _lpR + _flip;
            l[i] = inL + _mixS * wetL;
            r[i] = inR + _mixS * wetR;
            _w++;
        }
    }

    private static float Wrap(float p) => p >= 1f ? p - 1f : p;
    private static float Soft(float x) => 1.5f * FxMath.Tanh(x * (1f / 1.5f));

    private float Grain(float[] buf, float p)
    {
        float s = MathF.Sin(MathF.PI * p);
        return s * s * FxMath.ReadCubic(buf, _mask, _w, 2f + 2f * _lenS * p);
    }
}

/// <summary>Windowed-sinc half-band 2x oversampler (23 taps), polyphase up, decimating down.</summary>
internal sealed class HalfbandOversampler
{
    public const int Taps = 23;
    private const int K = (Taps + 1) / 2;
    private static readonly float[] H = Design();
    private readonly float[] _he = new float[K], _ho = new float[K];
    private readonly float[] _xh = new float[2 * K];
    private readonly float[] _vh = new float[2 * Taps];
    private int _xp, _vp;

    public HalfbandOversampler()
    {
        for (int k = 0; k < K; k++) { _he[k] = 2f * H[2 * k]; if (2 * k + 1 < Taps) _ho[k] = 2f * H[2 * k + 1]; }
    }

    private static float[] Design()
    {
        var h = new float[Taps];
        int c = (Taps - 1) / 2;
        double sum = 0;
        for (int n = 0; n < Taps; n++)
        {
            double t = (n - c) * 0.5, sinc = t == 0 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * n / (Taps - 1));
            h[n] = (float)(0.5 * sinc * w);
            sum += h[n];
        }
        for (int n = 0; n < Taps; n++) h[n] = (float)(h[n] / sum);
        return h;
    }

    public void Clear() { Array.Clear(_xh); Array.Clear(_vh); _xp = _vp = 0; }

    public void Up(float x, out float even, out float odd)
    {
        _xh[_xp] = x; _xh[_xp + K] = x;
        int newest = _xp + K; // index of newest; older samples at lower indices
        float e = 0f, o = 0f;
        for (int k = 0; k < K; k++) { float v = _xh[newest - k]; e += _he[k] * v; o += _ho[k] * v; }
        _xp = _xp + 1 == K ? 0 : _xp + 1;
        even = e; odd = o;
    }

    public float Down(float a, float b)
    {
        // Decimate on the even phase (sample "a"): total latency is exactly (Taps - 1) / 2 input samples.
        _vh[_vp] = a; _vh[_vp + Taps] = a;
        int newest = _vp + Taps;
        float y = 0f;
        for (int j = 0; j < Taps; j++) y += H[j] * _vh[newest - j];
        _vp = _vp + 1 == Taps ? 0 : _vp + 1;
        _vh[_vp] = b; _vh[_vp + Taps] = b;
        _vp = _vp + 1 == Taps ? 0 : _vp + 1;
        return y;
    }
}

/// <summary>Overdrive: exponential drive, 2x oversampled asymmetric soft clipper, drive-dependent tone and level compensation.</summary>
internal sealed class OverdriveFx : FxBase
{
    private readonly HalfbandOversampler _osL = new(), _osR = new();
    private readonly float[] _dryL, _dryR;
    private int _dp;
    private float _driveT, _driveS, _mixT, _mixS, _toneT, _toneS;
    private float _toneL, _toneR, _dcL, _dcR, _dcA;

    private const int Latency = (HalfbandOversampler.Taps - 1) / 2;
    private const float Bias = 0.08f;

    public OverdriveFx(float fs) : base(fs)
    {
        _dryL = new float[32]; _dryR = new float[32];
        _dcA = FxMath.OnePoleCoef(18f, fs);
    }

    protected override void OnParams()
    {
        _driveT = MathF.Pow(45f, Speed);
        _mixT = Amount;
        _toneT = FxMath.OnePoleCoef(12000f * MathF.Pow(0.3f, Speed), Fs); // more drive -> darker, tames the fizz
        if (Snap) { _driveS = _driveT; _mixS = _mixT; _toneS = _toneT; }
    }

    protected override void Clear()
    {
        _osL.Clear(); _osR.Clear(); Array.Clear(_dryL); Array.Clear(_dryR);
        _toneL = _toneR = _dcL = _dcR = 0f; _dp = 0;
    }

    private static float Shape(float x) => FxMath.Tanh(x + Bias) - FxMath.Tanh(Bias);

    public override void Process(Span<float> l, Span<float> r)
    {
        if (Snap) { _driveS = _driveT; _mixS = _mixT; _toneS = _toneT; Snap = false; }
        float sm = Smooth;
        for (int i = 0; i < l.Length; i++)
        {
            _driveS += (_driveT - _driveS) * sm;
            _mixS += (_mixT - _mixS) * sm;
            _toneS += (_toneT - _toneS) * sm;
            float comp = MathF.Pow(_driveS, -0.4f);
            float inL = l[i], inR = r[i];

            _osL.Up(inL * _driveS, out float aL, out float bL);
            _osR.Up(inR * _driveS, out float aR, out float bR);
            float yL = _osL.Down(Shape(aL), Shape(bL));
            float yR = _osR.Down(Shape(aR), Shape(bR));
            _toneL += _toneS * (yL - _toneL); _toneR += _toneS * (yR - _toneR);
            _dcL += _dcA * (_toneL - _dcL); _dcR += _dcA * (_toneR - _dcR);
            float wL = (_toneL - _dcL) * comp, wR = (_toneR - _dcR) * comp;

            // dry path delayed to match the oversampler's latency
            _dryL[_dp & 31] = inL; _dryR[_dp & 31] = inR;
            float dL = _dryL[(_dp - Latency) & 31], dR = _dryR[(_dp - Latency) & 31];
            _dp++;
            l[i] = dL + (wL - dL) * _mixS;
            r[i] = dR + (wR - dR) * _mixS;
        }
    }
}

/// <summary>Bit crusher + sample-rate reducer driven together by SPEED; AMOUNT = mix.</summary>
internal sealed class CrushFx : FxBase
{
    private float _levels, _ratio, _mixT, _mixS, _phase, _holdL, _holdR;

    public CrushFx(float fs) : base(fs) { }

    protected override void OnParams()
    {
        float bits = 16f - 14f * Speed;
        _levels = MathF.Pow(2f, bits - 1f);
        _ratio = MathF.Pow(0.04f, Speed); // 1 -> 0.04 of the sample rate
        _mixT = Amount;
        if (Snap) _mixS = _mixT;
    }

    protected override void Clear() { _phase = 0f; _holdL = _holdR = 0f; }

    public override void Process(Span<float> l, Span<float> r)
    {
        if (Snap) { _mixS = _mixT; Snap = false; }
        float sm = Smooth, q = _levels, iq = 1f / _levels;
        for (int i = 0; i < l.Length; i++)
        {
            _mixS += (_mixT - _mixS) * sm;
            _phase += _ratio;
            if (_phase >= 1f)
            {
                _phase -= 1f;
                _holdL = MathF.Round(Math.Clamp(l[i], -1f, 1f) * q) * iq;
                _holdR = MathF.Round(Math.Clamp(r[i], -1f, 1f) * q) * iq;
            }
            float dl = l[i], dr = r[i];
            l[i] = dl + (_holdL - dl) * _mixS;
            r[i] = dr + (_holdR - dr) * _mixS;
        }
    }
}

/// <summary>Tilt EQ: complementary low/high shelves around a pivot (150 Hz..6 kHz), +-4.5 dB per side = 9 dB tilt at the extremes.</summary>
internal sealed class TiltEqFx : FxBase
{
    private const int Step = 32;
    private Biquad _loL, _loR, _hiL, _hiR;
    private float _fT = 1000f, _fS = 1000f, _gT, _gS;

    public TiltEqFx(float fs) : base(fs) { _loL.SetIdentity(); _loR.SetIdentity(); _hiL.SetIdentity(); _hiR.SetIdentity(); }

    protected override void OnParams()
    {
        _fT = 150f * MathF.Pow(6000f / 150f, Speed);
        float b = (Amount * 127f - 63.5f) / 63.5f; // -1..+1
        _gT = Math.Clamp(b, -1f, 1f) * 4.5f;
        if (Math.Abs(_gT) < 0.02f) _gT = 0f;
        if (Snap) { _fS = _fT; _gS = _gT; }
    }

    protected override void Clear() { _loL.Clear(); _loR.Clear(); _hiL.Clear(); _hiR.Clear(); }

    private void Update()
    {
        if (_gS == 0f)
        {
            _loL.B0 = _hiL.B0 = _loR.B0 = _hiR.B0 = 1f;
            _loL.B1 = _loL.B2 = _loL.A1 = _loL.A2 = 0f; _hiL.B1 = _hiL.B2 = _hiL.A1 = _hiL.A2 = 0f;
            _loR.B1 = _loR.B2 = _loR.A1 = _loR.A2 = 0f; _hiR.B1 = _hiR.B2 = _hiR.A1 = _hiR.A2 = 0f;
            return;
        }
        // brighter (positive): lows down, highs up
        _loL.SetShelf(false, _fS, -_gS, Fs); _hiL.SetShelf(true, _fS, _gS, Fs);
        _loR.B0 = _loL.B0; _loR.B1 = _loL.B1; _loR.B2 = _loL.B2; _loR.A1 = _loL.A1; _loR.A2 = _loL.A2;
        _hiR.B0 = _hiL.B0; _hiR.B1 = _hiL.B1; _hiR.B2 = _hiL.B2; _hiR.A1 = _hiL.A1; _hiR.A2 = _hiL.A2;
    }

    public override void Process(Span<float> l, Span<float> r)
    {
        if (Snap) { _fS = _fT; _gS = _gT; Snap = false; }
        float sm = 1f - MathF.Pow(1f - Smooth, Step);
        for (int pos = 0; pos < l.Length; pos += Step)
        {
            _fS += (_fT - _fS) * sm;
            _gS += (_gT - _gS) * sm;
            if (Math.Abs(_gS - _gT) < 0.005f) _gS = _gT;
            Update();
            int end = Math.Min(pos + Step, l.Length);
            for (int i = pos; i < end; i++)
            {
                l[i] = _hiL.Process(_loL.Process(l[i]));
                r[i] = _hiR.Process(_loR.Process(r[i]));
            }
        }
    }
}

/// <summary>Stereo chorus: three modulated voices per channel, left and right LFOs in opposite phase.</summary>
internal sealed class ChorusFx : FxBase
{
    private readonly float[] _bl, _br;
    private readonly int _mask;
    private int _w;
    private float _rateT, _rateS, _depthT, _depthS, _mixT, _mixS;
    private readonly float[] _ph = { 0f, 1f / 3f, 2f / 3f };
    private static readonly float[] BaseMs = { 11f, 15f, 19f };

    public ChorusFx(float fs) : base(fs)
    {
        int len = FxMath.NextPow2((int)(0.08f * fs) + 16);
        _bl = new float[len]; _br = new float[len]; _mask = len - 1;
    }

    protected override void OnParams()
    {
        _rateT = 0.05f * MathF.Pow(100f, Speed);
        _depthT = (0.3f + 5.5f * Amount) * 0.001f * Fs;
        _mixT = Math.Min(1f, Amount * 3f) * 0.55f;
        if (Snap) { _rateS = _rateT; _depthS = _depthT; _mixS = _mixT; }
    }

    protected override void Clear() { Array.Clear(_bl); Array.Clear(_br); _w = 0; _ph[0] = 0f; _ph[1] = 1f / 3f; _ph[2] = 2f / 3f; }

    public override void Process(Span<float> l, Span<float> r)
    {
        if (Snap) { _rateS = _rateT; _depthS = _depthT; _mixS = _mixT; Snap = false; }
        float sm = Smooth;
        for (int i = 0; i < l.Length; i++)
        {
            _rateS += (_rateT - _rateS) * sm; _depthS += (_depthT - _depthS) * sm; _mixS += (_mixT - _mixS) * sm;
            float inc = _rateS / Fs;
            float ig = NextInGain();
            _bl[_w & _mask] = l[i] * ig; _br[_w & _mask] = r[i] * ig;
            float wl = 0f, wr = 0f;
            for (int v = 0; v < 3; v++)
            {
                _ph[v] += inc; if (_ph[v] >= 1f) _ph[v] -= 1f;
                float lfo = MathF.Sin(FxMath.TwoPi * _ph[v]);
                float baseD = BaseMs[v] * 0.001f * Fs;
                wl += FxMath.ReadCubic(_bl, _mask, _w, baseD + _depthS * lfo);
                wr += FxMath.ReadCubic(_br, _mask, _w, baseD - _depthS * lfo); // opposite-phase LFO on the right
            }
            _w++;
            wl *= 1f / 3f; wr *= 1f / 3f;
            l[i] += (wl - l[i]) * _mixS;
            r[i] += (wr - r[i]) * _mixS;
        }
    }
}
