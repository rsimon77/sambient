namespace Sambient.Dsp.Effects;

/// <summary>Reverb types of the original device: Small / Large / Infinity × L / M / H.</summary>
public enum ReverbType { Off, SmallL, SmallM, SmallH, LargeL, LargeM, LargeH, InfinityL, InfinityM, InfinityH }

/// <summary>
/// Send-bus shimmer reverb: input allpass diffusion + pre-delay feeding an 8-line feedback delay network
/// (Householder mixing, modulated delay lines, per-line damping and a soft limiter in the loop).
/// Shimmer = send signal (plus a slice of the reverb output, cascaded) through a dual-grain +12 st pitch shifter
/// into the reverb input. All methods are real-time safe (no allocation) and audio-thread only.
/// </summary>
public sealed class ShimmerReverb
{
    private const int N = 8;
    private const float BaseRate = 48000f;

    // Delay lengths (samples @ 48 kHz) per size class: Small (room), Large (hall), Infinity.
    private static readonly int[][] LineLengths =
    {
        new[] { 1087, 1291, 1523, 1747, 1993, 2237, 2503, 2791 },
        new[] { 1789, 2243, 2753, 3299, 3851, 4423, 5023, 5693 },
        new[] { 2909, 3517, 4219, 4909, 5701, 6451, 7229, 8087 },
    };

    // RT60 in seconds [size][tone L, M, H], measured at the loop's broadband level (damping shortens the highs further).
    private static readonly float[][] Rt60 =
    {
        new[] { 1.5f, 1.9f, 2.3f },
        new[] { 6.6f, 7.2f, 8.6f },
        new[] { 140f, 170f, 220f },
    };
    private static readonly float[] DampHz = { 1700f, 5000f, 11000f };
    private static readonly float[] DampHzInfinity = { 2600f, 6000f, 12000f };
    private static readonly float[] InLpHz = { 3200f, 9000f, 17000f };
    private static readonly float[] PreDelayMs = { 7f, 22f, 38f };
    private static readonly float[] ShimmerFbMax = { 0.40f, 0.45f, 0.30f };
    private static readonly int[] AllpassBase = { 142, 107, 379, 277, 163, 113, 349, 251 }; // L: first 4, R: last 4 (@ 48 kHz)

    private readonly float _fs;
    private readonly float[][] _line = new float[N][];
    private readonly int[] _mask = new int[N];
    private int _w; // shared write index (all lines advance together; each has its own mask)
    private readonly int[] _len = new int[N];
    private readonly float[] _g = new float[N];
    private readonly float[] _gTarget = new float[N];
    private readonly float[] _damp = new float[N];
    private readonly float[] _v = new float[N];
    private readonly float[] _modS = new float[N], _modC = new float[N], _modW = new float[N];
    private float _modDepth;

    private readonly AllpassStage[] _apL = new AllpassStage[4], _apR = new AllpassStage[4];
    private readonly float[] _pdL, _pdR;
    private readonly int _pdMask;
    private int _pdW;
    private float _pdDelay;

    private readonly OctaveUpShifter _shL, _shR;
    private float _shLpL1, _shLpL2, _shLpR1, _shLpR2, _shLpA;
    private float _shEnv, _shEnvDecay, _shFbS, _shFbMax, _lastWetL, _lastWetR;

    private float _inLpL, _inLpR, _inHpA, _inHpL, _inHpR;
    private float _dampTarget, _dampS, _injTarget, _injS, _smooth;
    private float _inLpTarget, _inLpS;

    private float _mixTarget, _mixGain;
    private float _outGain = 1f, _fadeStep;
    private bool _resetPending, _active = true;
    private ReverbType _pendingType = ReverbType.LargeM;
    private int _size = 1, _tone = 1;
    private float _flip = 1e-14f;

    public ShimmerReverb(float sampleRate)
    {
        _fs = sampleRate;
        SampleRate = sampleRate;
        float k = sampleRate / BaseRate;
        int maxLen = 0;
        foreach (var set in LineLengths) foreach (var l in set) maxLen = Math.Max(maxLen, (int)(l * k) + 1);
        int bufLen = FxMath.NextPow2(maxLen + 128);
        for (int i = 0; i < N; i++) { _line[i] = new float[bufLen]; _mask[i] = bufLen - 1; }
        for (int i = 0; i < 4; i++)
        {
            _apL[i] = new AllpassStage(Odd(AllpassBase[i] * k), 0.58f);
            _apR[i] = new AllpassStage(Odd(AllpassBase[4 + i] * k), 0.58f);
        }
        int pd = FxMath.NextPow2((int)(0.06f * sampleRate) + 8);
        _pdL = new float[pd]; _pdR = new float[pd]; _pdMask = pd - 1;
        _shL = new OctaveUpShifter(sampleRate);
        _shR = new OctaveUpShifter(sampleRate, 0.081f); // slightly different grain: decorrelates the two channels
        _shLpA = FxMath.OnePoleCoef(7000f, sampleRate);
        _inHpA = FxMath.OnePoleCoef(90f, sampleRate);
        _shEnvDecay = MathF.Exp(-1f / (3f * sampleRate));
        _smooth = FxMath.SmoothCoef(0.03f, sampleRate);
        _fadeStep = 1f / (0.012f * sampleRate);
        SetMix(64f);
        Apply(ReverbType.LargeM);
        Clear();
        SnapSmoothers();
    }

    public float SampleRate { get; }
    public ReverbType Type { get; private set; } = ReverbType.LargeM;

    private static int Odd(float x) => ((int)MathF.Round(x)) | 1;

    /// <summary>Changes the algorithm/voicing. Real-time safe. Same size class = smooth retune; size change = short fade + reset.</summary>
    public void SetType(ReverbType type)
    {
        if (type == Type && !_resetPending) return;
        Type = type;
        bool sameClass = type != ReverbType.Off && _active && !_resetPending && SizeOf(type) == _size;
        if (sameClass) Retune(type);
        else { _pendingType = type; _resetPending = true; }
    }

    private static int SizeOf(ReverbType t) => ((int)t - 1) / 3;
    private static int ToneOf(ReverbType t) => ((int)t - 1) % 3;

    /// <summary>Wet return level, hardware units 0..127 ("shift + reverb" knob).</summary>
    public void SetMix(float mix0To127)
    {
        float x = FxMath.Clamp01(mix0To127 / 127f);
        _mixTarget = x * MathF.Sqrt(x) * 1.5f; // perceptual curve, 127 = wet level about equal to the dry-equivalent level
    }

    /// <summary>
    /// Click-free "reverb reset" (R.RST on a pattern change): fades the wet output out (~12 ms), clears all state and the
    /// next block fades back in. Real-time safe.
    /// </summary>
    public void SoftReset()
    {
        if (Type == ReverbType.Off || !_active) { Clear(); return; }
        if (!_resetPending) { _pendingType = Type; _resetPending = true; }
    }

    /// <summary>Clears all internal state immediately (offline use / tests; a running reverb should use <see cref="SoftReset"/>).</summary>
    public void Reset()
    {
        Clear();
        _outGain = 1f;
        _resetPending = false;
        _active = Type != ReverbType.Off;
        if (_active) { Apply(Type); SnapSmoothers(); }
    }

    private void Clear()
    {
        for (int i = 0; i < N; i++)
        {
            Array.Clear(_line[i]);
            _damp[i] = 0f;
            float ph = i * 0.9f + 0.3f;
            _modS[i] = MathF.Sin(ph); _modC[i] = MathF.Cos(ph);
        }
        foreach (var a in _apL) a.Clear();
        foreach (var a in _apR) a.Clear();
        Array.Clear(_pdL); Array.Clear(_pdR);
        _shL.Clear(); _shR.Clear();
        _shLpL1 = _shLpL2 = _shLpR1 = _shLpR2 = 0f;
        _shEnv = _shFbS = _lastWetL = _lastWetR = 0f;
        _inLpL = _inLpR = _inHpL = _inHpR = 0f;
        _w = 0; _pdW = 0;
    }

    private void SnapSmoothers()
    {
        for (int i = 0; i < N; i++) _g[i] = _gTarget[i];
        _dampS = _dampTarget; _injS = _injTarget; _inLpS = _inLpTarget;
        _mixGain = _mixTarget;
    }

    /// <summary>Full (re)configuration for a type: line lengths, modulation and decay targets.</summary>
    private void Apply(ReverbType t)
    {
        if (t == ReverbType.Off) { _active = false; return; }
        _active = true;
        _size = SizeOf(t);
        float k = _fs / BaseRate;
        for (int i = 0; i < N; i++) _len[i] = Odd(LineLengths[_size][i] * k);
        _modDepth = (_size == 2 ? 20f : 11f) * k;
        for (int i = 0; i < N; i++) _modW[i] = FxMath.TwoPi * (0.17f + 0.071f * i) / _fs;
        _pdDelay = PreDelayMs[_size] * 0.001f * _fs;
        Retune(t);
    }

    /// <summary>Retargets decay / damping / tone without touching buffers (smoothed per sample).</summary>
    private void Retune(ReverbType t)
    {
        _tone = ToneOf(t);
        float rt = Rt60[_size][_tone];
        float gsq = 0f;
        for (int i = 0; i < N; i++)
        {
            float g = MathF.Pow(10f, -3f * _len[i] / (_fs * rt));
            _gTarget[i] = Math.Min(g, 0.99995f);
            gsq += g * g;
        }
        gsq /= N;
        _dampTarget = FxMath.OnePoleCoef((_size == 2 ? DampHzInfinity : DampHz)[_tone], _fs);
        _inLpTarget = FxMath.OnePoleCoef(InLpHz[_tone], _fs);
        // Energy-normalised injection: total impulse-response energy is about the same for all decay times.
        _injTarget = MathF.Sqrt(Math.Max(1f - gsq, 1e-6f)) * 1.6f;
        _shFbMax = ShimmerFbMax[_size];
    }

    /// <summary>
    /// Processes one block. <paramref name="reverbInL"/>/<paramref name="reverbInR"/> = summed per-layer reverb sends,
    /// <paramref name="shimmerInL"/>/<paramref name="shimmerInR"/> = summed per-layer shimmer sends (fed through a
    /// +12 semitone pitch shifter into the reverb). <paramref name="wetL"/>/<paramref name="wetR"/> are OVERWRITTEN with
    /// the wet signal already scaled by the mix level. All spans have the same length.
    /// </summary>
    public void Process(ReadOnlySpan<float> reverbInL, ReadOnlySpan<float> reverbInR,
                        ReadOnlySpan<float> shimmerInL, ReadOnlySpan<float> shimmerInR,
                        Span<float> wetL, Span<float> wetR)
    {
        int n = Math.Min(Math.Min(wetL.Length, wetR.Length), Math.Min(reverbInL.Length, reverbInR.Length));
        n = Math.Min(n, Math.Min(shimmerInL.Length, shimmerInR.Length));
        float sm = _smooth;
        float lim = 2f;
        const float outScale = 0.35355339f;

        for (int s = 0; s < n; s++)
        {
            // Type-change state machine: fade out, reconfigure + clear, fade in.
            if (_resetPending)
            {
                _outGain -= _fadeStep;
                if (_outGain <= 0f)
                {
                    _outGain = 0f;
                    Clear();
                    Apply(_pendingType);
                    SnapSmoothers();
                    _resetPending = false;
                }
            }
            else if (_outGain < 1f) _outGain = Math.Min(1f, _outGain + _fadeStep);

            if (!_active) { wetL[s] = 0f; wetR[s] = 0f; continue; }

            _mixGain += (_mixTarget - _mixGain) * sm;
            _dampS += (_dampTarget - _dampS) * sm;
            _injS += (_injTarget - _injS) * sm;
            _inLpS += (_inLpTarget - _inLpS) * sm;

            // --- shimmer path: send + cascaded slice of the reverb output -> +12 st -> high-cut
            float shInL = shimmerInL[s], shInR = shimmerInR[s];
            float env = MathF.Abs(shInL) + MathF.Abs(shInR);
            _shEnv = env > _shEnv ? env : _shEnv * _shEnvDecay;
            float fbT = _shFbMax * FxMath.Clamp01(_shEnv * 6f);
            _shFbS += (fbT - _shFbS) * 0.0005f;
            float pL = _shL.Process(FxMath.Tanh(shInL + _shFbS * _lastWetL));
            float pR = _shR.Process(FxMath.Tanh(shInR + _shFbS * _lastWetR));
            _shLpL1 += _shLpA * (pL - _shLpL1); _shLpL2 += _shLpA * (_shLpL1 - _shLpL2);
            _shLpR1 += _shLpA * (pR - _shLpR1); _shLpR2 += _shLpA * (_shLpR1 - _shLpR2);

            // --- input conditioning: high-pass (rumble), tone low-pass
            float xL = reverbInL[s] + _shLpL2, xR = reverbInR[s] + _shLpR2;
            _inHpL += _inHpA * (xL - _inHpL); xL -= _inHpL;
            _inHpR += _inHpA * (xR - _inHpR); xR -= _inHpR;
            _inLpL += _inLpS * (xL - _inLpL); xL = _inLpL;
            _inLpR += _inLpS * (xR - _inLpR); xR = _inLpR;

            // --- pre-delay and diffusion
            _pdL[_pdW & _pdMask] = xL; _pdR[_pdW & _pdMask] = xR;
            xL = FxMath.ReadLinear(_pdL, _pdMask, _pdW, _pdDelay);
            xR = FxMath.ReadLinear(_pdR, _pdMask, _pdW, _pdDelay);
            _pdW++;
            for (int a = 0; a < 4; a++) { xL = _apL[a].Process(xL); xR = _apR[a].Process(xR); }
            _flip = -_flip; // inaudible +-1e-14 dither keeps the recursive states out of the denormal range
            xL = xL * _injS + _flip; xR = xR * _injS + _flip;

            // --- FDN: read modulated taps, damp, soft limit, apply loop gain
            float sum = 0f, oL = 0f, oR = 0f;
            for (int i = 0; i < N; i++)
            {
                float ms = _modS[i], mc = _modC[i], mw = _modW[i];
                ms += mw * mc; mc -= mw * ms;
                _modS[i] = ms; _modC[i] = mc;
                float d = _len[i] + _modDepth * ms;
                float tap = FxMath.ReadLinear(_line[i], _mask[i], _w, d);
                _damp[i] += _dampS * (tap - _damp[i]);
                float dl = _damp[i];
                float y = lim * FxMath.Tanh(dl * (1f / lim)); // soft limiter inside the loop
                _g[i] += (_gTarget[i] - _g[i]) * sm;
                _v[i] = y * _g[i];
                sum += _v[i];
                // decorrelated stereo taps (two orthogonal sign patterns)
                if ((i & 1) == 0) { oL += y; oR += y; } else { oL += y; oR -= y; }
            }
            float h = sum * (2f / N);
            for (int i = 0; i < N; i++)
            {
                float inj = (i & 1) == 0 ? xL : xR;
                if ((i & 2) != 0) inj = -inj;
                _line[i][_w & _mask[i]] = _v[i] - h + inj;
            }
            _w++;

            float wl = oL * outScale, wr = oR * outScale;
            _lastWetL = wl; _lastWetR = wr;
            float gain = _mixGain * _outGain;
            wetL[s] = wl * gain;
            wetR[s] = wr * gain;
        }
    }
}
