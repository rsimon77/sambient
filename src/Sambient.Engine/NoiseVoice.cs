using Sambient.Dsp.Envelopes;
using Sambient.Dsp.Filters;
using Sambient.Dsp.Noise;

namespace Sambient.Engine;

/// <summary>Per-sub-block settings shared by all voices of the NOISE layer.</summary>
internal struct NoiseBlock
{
    public float PitchSemis;        // octave*12 + pitch bend (added to note - root)
    public double SrcRatio;         // sample.SampleRate / output rate (1 when no sample)
    public float NoiseGain, SampleGain;
    public FilterMode Filter;
    public float CutoffHz, Q, BandwidthOct;
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

    public NoiseVoice(float sampleRate, int seed)
    {
        _fs = sampleRate;
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
        _env.SetTimes(_attackMs, _releaseMs);
        _env.NoteOn();
    }

    public void Retrigger(int velocity, long age)
    {
        Age = age;
        Gate = true; SustainDeferred = false;
        if (Pending) { PendingVelocity = velocity; return; }
        _velGain = SynthVoice.VelocityGain(velocity);
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

    /// <summary>Adds this voice's stereo output for <paramref name="n"/> samples into the two mix buffers.</summary>
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
            _env.SetTimes(_attackMs, _releaseMs);
            _env.NoteOn();
        }
        if (!_env.IsActive)
        {
            if (!Pending) Active = false;
            return;
        }

        // root key C4 plays the sample at its original pitch
        double rate = NoiseLayer.PlaybackRate(b.SrcRatio, Note - NoiseLayer.RootNote + b.PitchSemis);
        _player.SetRate(rate);
        if (_xfade > 0) _playerOld.SetRate(rate);
        _filterL.Mode = _filterR.Mode = b.Filter;
        if (b.Filter == FilterMode.BandPass)
        {
            _filterL.SetBandwidthOctaves(b.CutoffHz, b.BandwidthOct);
            _filterR.SetBandwidthOctaves(b.CutoffHz, b.BandwidthOct);
        }
        else if (b.Filter != FilterMode.Off)
        {
            _filterL.SetParameters(b.CutoffHz, b.Q);
            _filterR.SetParameters(b.CutoffHz, b.Q);
        }

        float ng = b.NoiseGain * WhiteGain, sg = b.SampleGain, gain = _velGain;
        for (int i = 0; i < n; i++)
        {
            _player.Next(out float sl, out float sr);
            if (_xfade > 0)
            {
                _playerOld.Next(out float ol, out float orr);
                float t = (float)_xfade / SampleFadeSamples;    // 1 -> 0
                sl += (ol - sl) * t;
                sr += (orr - sr) * t;
                _xfade--;
            }
            float l = sl * sg + White(ref _rngL) * ng;
            float r = sr * sg + White(ref _rngR) * ng;
            l = _filterL.Process(l);
            r = _filterR.Process(r);
            float e = _env.Next() * gain;
            mixL[i] += l * e;
            mixR[i] += r * e;
        }
        if (!_env.IsActive && !Pending) Active = false;
    }
}
