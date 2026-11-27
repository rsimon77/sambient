using Sambient.Dsp.Envelopes;
using Sambient.Dsp.Filters;
using Sambient.Dsp.Wavetables;

namespace Sambient.Engine;

/// <summary>Per-sub-block settings shared by all voices of a layer (computed once per sub-block).</summary>
internal struct VoiceBlock
{
    public float Harmonic0, HarmonicStep;   // HARMONIC at the first sample and per-sample increment
    public float PitchSemis;                // octave*12 + pitch bend, added to the MIDI note
    public FilterMode Filter;
    public float CutoffHz, Q, BandwidthOct;
}

/// <summary>
/// One voice: WavetableOscillator -> StateVariableFilter (mono) -> ArEnvelope x velocity gain. Output is mono; the
/// layer applies level and pan to the summed voices. Audio-thread only; allocation-free after construction.
/// Stolen voices are faded out with the envelope's 5 ms Kill and then start the pending note (see <see cref="Steal"/>).
/// </summary>
internal sealed class SynthVoice
{
    private const int WaveFadeSamples = 1024;    // ~21 ms at 48 kHz

    private readonly float _fs;
    private WavetableOscillator _osc, _oscOld;
    private StateVariableFilter _filter;
    private ArEnvelope _env;
    private Wavetable? _table;
    private float _velGain;
    private int _xfade, _xfadeTotal;
    private float _attackMs, _releaseMs;

    public int Note = -1;
    /// <summary>Voice is allocated (sounding, releasing or waiting for its steal fade to end).</summary>
    public bool Active;
    /// <summary>The key (or the sustain pedal) is keeping the envelope open.</summary>
    public bool Gate;
    /// <summary>Key released while the sustain pedal was down: release happens at pedal-up.</summary>
    public bool SustainDeferred;
    /// <summary>Stolen: kill fade running, the new note starts when it ends.</summary>
    public bool Pending;
    public int PendingVelocity;
    public long Age;

    public bool IsReleasing => Active && !Gate && !Pending;

    public SynthVoice(float sampleRate)
    {
        _fs = sampleRate;
        _filter = new StateVariableFilter(sampleRate);
        _env = new ArEnvelope(sampleRate);
    }

    public void SetTimes(float attackMs, float releaseMs)
    {
        _attackMs = attackMs; _releaseMs = releaseMs;
        _env.SetTimes(attackMs, releaseMs);
    }

    public static float VelocityGain(int velocity)
    {
        float v = Math.Clamp(velocity, 1, 127) / 127f;
        float db = -30f + 30f * MathF.Pow(v, 1.5f);      // gentle curve, -30 dB .. 0 dB
        return MathF.Pow(10f, db * 0.05f);
    }

    /// <summary>Starts a note on a free voice.</summary>
    public void Start(Wavetable? table, int note, int velocity, long age)
    {
        _table = table;
        _osc.SetWave(table!);
        _xfade = 0;
        Note = note; Age = age;
        Active = true; Gate = true; SustainDeferred = false; Pending = false;
        _velGain = VelocityGain(velocity);
        _filter.Reset();
        _env.SetTimes(_attackMs, _releaseMs);
        _env.NoteOn();
    }

    /// <summary>Same note again: re-opens the envelope from its current level (no click, no filter reset).</summary>
    public void Retrigger(int velocity, long age)
    {
        Age = age;
        Gate = true; SustainDeferred = false;
        if (Pending) { PendingVelocity = velocity; return; }
        _velGain = VelocityGain(velocity);
        _env.SetTimes(_attackMs, _releaseMs);
        _env.NoteOn();
    }

    /// <summary>
    /// Takes this (sounding) voice over for another note: the old sound is killed with a ~5 ms fade, then the new note
    /// starts on the same voice (so stealing adds at most ~5 ms of latency but never clicks).
    /// </summary>
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
        if (Pending) { Pending = false; return; }   // never started: just let the kill fade finish
        _env.NoteOff();
    }

    /// <summary>Switches wavetable; active voices crossfade old -> new over ~21 ms.</summary>
    public void SwapWave(Wavetable? table)
    {
        if (Active && !Pending && _env.IsActive && _table != null)
        {
            _oscOld = _osc;                // intentional struct copy: old wave keeps playing from the same phase
            _xfade = _xfadeTotal = WaveFadeSamples;
        }
        else _xfade = 0;
        _table = table;
        _osc.SetWave(table!);
    }

    /// <summary>Adds this voice's mono output for <paramref name="n"/> samples into <paramref name="mix"/>.</summary>
    public void Process(Span<float> mix, int n, in VoiceBlock b)
    {
        if (Pending && !_env.IsActive)
        {
            var table = _table;
            int vel = PendingVelocity;
            Pending = false;
            _osc.SetWave(table!);
            _xfade = 0;
            _velGain = VelocityGain(vel);
            _filter.Reset();
            _env.SetTimes(_attackMs, _releaseMs);
            _env.NoteOn();
        }
        if (!_env.IsActive)
        {
            if (!Pending) Active = false;
            return;
        }

        double hz = 440.0 * Math.Pow(2.0, (Note + b.PitchSemis - 69.0) / 12.0);
        _osc.SetFrequency(hz, _fs);
        if (_xfade > 0) _oscOld.SetFrequency(hz, _fs);
        _filter.Mode = b.Filter;
        if (b.Filter == FilterMode.BandPass) _filter.SetBandwidthOctaves(b.CutoffHz, b.BandwidthOct);
        else if (b.Filter != FilterMode.Off) _filter.SetParameters(b.CutoffHz, b.Q);

        float h = b.Harmonic0;
        float gain = _velGain;
        for (int i = 0; i < n; i++)
        {
            _osc.SetPosition(h);
            float s = _osc.Next();
            if (_xfade > 0)
            {
                _oscOld.SetPosition(h);
                float o = _oscOld.Next();
                float t = (float)_xfade / _xfadeTotal;      // 1 -> 0
                s += (o - s) * t;
                _xfade--;
            }
            h += b.HarmonicStep;
            s = _filter.Process(s);
            mix[i] += s * _env.Next() * gain;
        }
        if (!_env.IsActive && !Pending) Active = false;
    }

    public void Kill()
    {
        _env.Kill();
        Gate = false; SustainDeferred = false; Pending = false;
    }
}
