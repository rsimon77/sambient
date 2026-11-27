using Sambient.Dsp.Envelopes;
using Sambient.Dsp.Filters;
using Sambient.Dsp.Noise;
using Sambient.Dsp.Params;

namespace Sambient.Engine;

/// <summary>Per-sub-block settings shared by all voices of the NOISE layer.</summary>
internal struct NoiseBlock
{
    public float PitchSemis;        // octave*12 + pitch bend (added to note - root)
    public double SrcRatio;         // sample.SampleRate / output rate (1 when no sample)
    public float NoiseGain, SampleGain;
    public FilterMode Filter;
    public float CutoffHz, Q, BandwidthOct; // legacy (voices derive them from the knobs below so LFOs can modulate them)
    public float CutoffKnob, ResoKnob;      // smoothed layer CUTOFF / RESO knob values 0..127
    public float Pan;                       // smoothed layer PAN -63..63 (the voice applies the pan law, see PanSpread)
    public float BalanceKnob;               // smoothed HARMONIC = noise (0) <-> sample (127) balance
    public LayerModulation Mod;             // layer-wide MOD / LFO state
}

/// <summary>
/// One NOISE-layer voice: stereo SamplePlayer + stereo white noise (balance) -> stereo SVF -> AR x velocity.
/// Output is stereo. Voice stealing uses the same kill-fade + pending-note scheme as <see cref="SynthVoice"/>.
/// Audio-thread only; allocation-free after construction.
/// </summary>
internal sealed class NoiseVoice
{
    private const int SampleFadeSamples = 1024;
    private const float WhiteGain = 0.4f;           // uniform white noise has RMS 0.58: keep it near the sample level

    private readonly float _fs;
    private SamplePlayer _player, _playerOld;
    private StateVariableFilter _filterL, _filterR;
    private ArEnvelope _env;
    private NoiseSample? _sample;
    private SamplePlayMode _mode;
    private float _velGain;
    private int _xfade;
    private float _attackMs, _releaseMs;
    private uint _rngL, _rngR;

    public int Note = -1;
    public bool Active;
    public bool Gate;
    public bool SustainDeferred;
    public bool Pending;
    public int PendingVelocity;
    public long Age;

    public bool IsReleasing => Active && !Gate && !Pending;

    // ---- Voice-mode contract (used by the layer voice modes) ----
    /// <summary>Glide time in ms for pitch moves made with <see cref="SlideTo"/> (0 = instant).</summary>
    public float GlideMs;
    /// <summary>Moves the sounding pitch to <paramref name="note"/> over <see cref="GlideMs"/> WITHOUT retriggering the
    /// envelope or the LFOs (legato / mono glide). Exponential in the semitone domain: ~95 % of the interval after
    /// <see cref="GlideMs"/> (time constant GlideMs/3). Audio thread only.</summary>
    public void SlideTo(int note)
    {
        Note = note;
        if (GlideMs <= 0.5f) _curNote = note;
    }
    /// <summary>Static pitch offset of this voice in cents (unison detune spread).</summary>
    public float DetuneOffsetCents;
    /// <summary>Static stereo spread offset -1..1 for this voice (unison width), added to the layer pan.</summary>
    public float PanSpread;

    /// <summary>Test hook: the sounding pitch (fractional MIDI note).</summary>
    internal float SoundingNote => _curNote;

    private readonly VoiceModulator _vm;
    private float _curNote;
    private float _ng, _sg;
    private float _pl, _pr;
    private bool _init, _notify;

    public NoiseVoice(float sampleRate, int seed)
    {
        _fs = sampleRate;
        _vm = new VoiceModulator(sampleRate, (uint)(seed + 1) * 104729u + 7u);
        _filterL = new StateVariableFilter(sampleRate);
        _filterR = new StateVariableFilter(sampleRate);
        _env = new ArEnvelope(sampleRate);
        _rngL = 0x9E3779B9u * (uint)(seed * 2 + 1) | 1u;
        _rngR = 0x85EBCA6Bu * (uint)(seed * 2 + 2) | 1u;
    }

    public void SetTimes(float attackMs, float releaseMs)
    {
        _attackMs = attackMs; _releaseMs = releaseMs;
        _env.SetTimes(attackMs, releaseMs);
    }

    public void SetMode(SamplePlayMode mode)
    {
        _mode = mode;
        _player.SetMode(mode);
    }

    public void Start(NoiseSample? sample, int note, int velocity, long age)
    {
        _sample = sample;
        _player.SetSample(sample);
        _player.SetMode(_mode);
        _player.Start();
        _xfade = 0;
        Note = note; Age = age;
        Active = true; Gate = true; SustainDeferred = false; Pending = false;
        _velGain = SynthVoice.VelocityGain(velocity);
        _filterL.Reset(); _filterR.Reset();
        _curNote = note; _init = false; _vm.Retrigger(); _notify = true;
        _env.SetTimes(_attackMs, _releaseMs);
        _env.NoteOn();
    }

    public void Retrigger(int velocity, long age)
    {
        Age = age;
        Gate = true; SustainDeferred = false;
        if (Pending) { PendingVelocity = velocity; return; }
        _velGain = SynthVoice.VelocityGain(velocity);
        _vm.Retrigger(); _notify = true;
        _env.SetTimes(_attackMs, _releaseMs);
        _env.NoteOn();
    }

    public void Steal(int note, int velocity, long age)
    {
        Note = note; Age = age;
        Pending = true; PendingVelocity = velocity;
        Gate = true; SustainDeferred = false;
        _env.Kill();
    }

    public void Release()
    {
        Gate = false; SustainDeferred = false;
        if (Pending) { Pending = false; return; }
        _env.NoteOff();
    }

    /// <summary>Switches the sample; an already sounding voice crossfades old -> new over ~21 ms.</summary>
    public void SwapSample(NoiseSample? sample)
    {
        if (Active && !Pending && _env.IsActive && _sample != null)
        {
            _playerOld = _player;           // intentional struct copy: the old sample keeps playing from the same position
            _xfade = SampleFadeSamples;
        }
        else _xfade = 0;
        _sample = sample;
        _player.SetSample(sample);
        _player.SetMode(_mode);
        _player.Start();
    }

    public void Kill()
    {
        _env.Kill();
        Gate = false; SustainDeferred = false; Pending = false;
    }

    private float White(ref uint state)
    {
        uint x = state;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        state = x;
        return (int)x * (1f / 2147483648f);
    }

    /// <summary>Adds this voice's stereo output (after per-voice pan) for <paramref name="n"/> samples into the two mix buffers.</summary>
    public void Process(Span<float> mixL, Span<float> mixR, int n, in NoiseBlock b)
    {
        if (Pending && !_env.IsActive)
        {
            int vel = PendingVelocity;
            Pending = false;
            _player.SetSample(_sample);
            _player.SetMode(_mode);
            _player.Start();
            _xfade = 0;
            _velGain = SynthVoice.VelocityGain(vel);
            _filterL.Reset(); _filterR.Reset();
            _curNote = Note; _init = false; _vm.Retrigger(); _notify = true;
            _env.SetTimes(_attackMs, _releaseMs);
            _env.NoteOn();
        }
        if (!_env.IsActive)
        {
            if (!Pending) Active = false;
            return;
        }

        int done = 0;
        while (done < n)
        {
            int c = Math.Min(VoiceModulator.Cap, n - done);
            ProcessChunk(mixL.Slice(done, c), mixR.Slice(done, c), c, done, in b);
            done += c;
        }
        if (!_env.IsActive && !Pending) Active = false;
    }

    private void ProcessChunk(Span<float> mixL, Span<float> mixR, int n, int offset, in NoiseBlock b)
    {
        bool legacy = b.Mod == null;     // LEGACY: layer applies pan / balance / cutoff itself (remove after layer migration)
        var m = b.Mod ?? LayerModulation.Neutral;
        var vm = _vm;
        if (_notify) { _notify = false; m.NoteStarted(); }
        vm.Prepare(m, n, offset);
        int mid = n >> 1;

        if (_curNote != Note)
        {
            if (GlideMs <= 0.5f) _curNote = Note;
            else
            {
                float k = 1f - MathF.Exp(-3f * n / (GlideMs * 0.001f * _fs));
                _curNote += (Note - _curNote) * k;
                if (MathF.Abs(Note - _curNote) < 0.002f) _curNote = Note;
            }
        }

        // pitch: note - root + octave/bend + PITCH + detune + unison offset + TUNE LFO + MOD (+-1 semitone at depth 127, depth^2)
        float semisP = ParamCurves.PitchSemitones(m.PitchUnits + vm.Pitch);
        float det = ParamCurves.DetuneCents(Math.Clamp(m.DetuneKnob + vm.DtFb, 0f, 127f)) * 0.01f;
        float modSemis = vm.ModD > 0f ? vm.ModD * vm.ModD * vm.Mod[mid] : 0f;
        float semis = _curNote - NoiseLayer.RootNote + b.PitchSemis + semisP + det + DetuneOffsetCents * 0.01f + vm.Tune + modSemis;
        double rate = NoiseLayer.PlaybackRate(b.SrcRatio, semis);
        _player.SetRate(rate);
        if (_xfade > 0) _playerOld.SetRate(rate);

        _filterL.Mode = _filterR.Mode = b.Filter;
        if (b.Filter != FilterMode.Off)
        {
            float ck = Math.Clamp(b.CutoffKnob + vm.Cutoff, 0f, 127f), rk = Math.Clamp(b.ResoKnob + vm.Reso, 0f, 127f);
            float cHz = legacy ? b.CutoffHz : ParamCurves.CutoffHz(ck);
            if (b.Filter == FilterMode.BandPass)
            {
                float bw = legacy ? b.BandwidthOct : ParamCurves.BandpassOctaves(rk);
                _filterL.SetBandwidthOctaves(cHz, bw);
                _filterR.SetBandwidthOctaves(cHz, bw);
            }
            else
            {
                float q = legacy ? b.Q : ParamCurves.ResonanceQ(rk);
                _filterL.SetParameters(cHz, q);
                _filterR.SetParameters(cHz, q);
            }
        }

        // HARMONIC = noise <-> sample balance (LFO HARMONIC adds frames = knob units), gains ramped over the chunk
        float bal = Math.Clamp(b.BalanceKnob + (vm.HasHarm ? vm.Harm[mid] : 0f), 0f, 127f);
        float t = bal * (1f / 127f) * (MathF.PI * 0.5f);
        float tng = MathF.Cos(t) * WhiteGain, tsg = MathF.Sin(t);
        if (legacy) { tng = b.NoiseGain * WhiteGain; tsg = b.SampleGain; }
        float panKnob = Math.Clamp(b.Pan + PanSpread * 63f + vm.Pan, -63f, 63f);
        var (tpl, tpr) = legacy ? (1f, 1f) : ParamCurves.PanGains(panKnob);
        if (!_init) { _ng = tng; _sg = tsg; _pl = tpl; _pr = tpr; _init = true; }
        float ngs = (tng - _ng) / n, sgs = (tsg - _sg) / n, pls = (tpl - _pl) / n, prs = (tpr - _pr) / n;

        float gain = _velGain;
        bool hasOsc = vm.HasOscLvl, hasLvl = vm.HasLvl;
        float ng = _ng, sg = _sg, pl = _pl, pr = _pr;
        for (int i = 0; i < n; i++)
        {
            _player.Next(out float sl, out float sr);
            if (_xfade > 0)
            {
                _playerOld.Next(out float ol, out float orr);
                float xt = (float)_xfade / SampleFadeSamples;    // 1 -> 0
                sl += (ol - sl) * xt;
                sr += (orr - sr) * xt;
                _xfade--;
            }
            float l = sl * sg + White(ref _rngL) * ng;
            float r = sr * sg + White(ref _rngR) * ng;
            if (hasOsc) { float og = vm.OscLvl[i]; l *= og; r *= og; }
            l = _filterL.Process(l);
            r = _filterR.Process(r);
            float e = _env.Next() * gain;
            if (hasLvl) e *= vm.Lvl[i];
            mixL[i] += l * e * pl;
            mixR[i] += r * e * pr;
            ng += ngs; sg += sgs; pl += pls; pr += prs;
        }
        _ng = tng; _sg = tsg; _pl = tpl; _pr = tpr;
    }
}
