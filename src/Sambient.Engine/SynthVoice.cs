using Sambient.Dsp.Envelopes;
using Sambient.Dsp.Filters;
using Sambient.Dsp.Modulation;
using Sambient.Dsp.Params;
using Sambient.Dsp.Wavetables;

namespace Sambient.Engine;

/// <summary>Per-sub-block settings shared by all voices of a layer (computed once per sub-block).</summary>
internal struct VoiceBlock
{
    public float Harmonic0, HarmonicStep;   // HARMONIC at the first sample and per-sample increment
    public float PitchSemis;                // octave*12 + pitch bend, added to the MIDI note
    public FilterMode Filter;
    public float CutoffHz, Q, BandwidthOct; // legacy (voices now derive them from the knobs below so LFOs can modulate them)
    public float CutoffKnob, ResoKnob;      // smoothed layer CUTOFF / RESO knob values 0..127
    public float Pan;                       // smoothed layer PAN -63..63 (the voice applies the pan law, see PanSpread)
    public LayerModulation Mod;             // layer-wide structure / MOD / LFO state (clocks, send modulation)
}

/// <summary>
/// One voice of a synth layer: 1..3 <see cref="WavetableOscillator"/>s arranged by the layer's <see cref="Structure"/>
/// (Yin/Yang model, PLAN.md 4.2) -> structure output stage -> StateVariableFilter -> ArEnvelope x velocity x LEVEL-LFO ->
/// per-voice pan into the layer's stereo mix. Audio-thread only; allocation-free after construction.
///
/// Structures (osc 1 at the note pitch; PITCH = +-12 st in 5-cent steps; detune = signed-square law +-50 cents):
///   DRONE1  2 Yang | osc2 = note + PITCH + detune + MOD(detune, +-1 st at depth 127, depth^2) | balance osc1 &lt;-&gt; osc2
///   DRONE2  Yang + Yin | osc2 = note + PITCH + detune | MOD pushes Yang HARMONIC +, Yin -  | balance osc1 &lt;-&gt; osc2
///   PAD1    Yang + 2 Yin | Yin pitches = note + PITCH +- detune (spread) | MOD on HARMONIC | balance Yin &lt;-&gt; Yang
///   PAD2    2 Yang + Yin | osc2 = osc1 + 3 cent fixed chorus, osc3 (Yin) = note + PITCH + detune | balance osc1,2 &lt;-&gt; osc3
///   ATMOS1  1 Yang (costs 3 in the oscillator budget) | pitch = note + PITCH + detune | MOD = ring modulation of the output
///           out *= (1 - d) + d * lfo  (d = MOD depth/127: tremolo at low rates, metallic ring mod up to 2 kHz) | no balance
///   ATMOS2  carrier (WAVE table, HARMONIC position) phase-modulated by a SINE modulator with self-feedback |
///           PITCH = modulator ratio 0.50..15.99 (1.00 at 0) | balance = FM amount (index 0..10 rad, square law) |
///           detune = modulator feedback (0..1.6 rad) | MOD sweeps the ratio +-1 octave. The FM index is limited per note so
///           the highest sideband stays below 0.45 fs (no oversampling, keeps it musical and alias-free).
/// Balance gains are equal-power (centre 63.5), changes are ramped over each 32-sample chunk (no zipper noise).
///
/// Structure changes while sounding: the voice dips its output to zero over 10 ms, switches structure, and fades back in
/// over 10 ms (click-free; the new structure is audible after ~10 ms). New notes use the current structure immediately.
/// WAVE changes crossfade old -> new in every oscillator (~21 ms). Stolen voices are faded out with the envelope's 5 ms Kill
/// and then start the pending note (see <see cref="Steal"/>).
/// </summary>
internal sealed class SynthVoice
{
    private const int WaveFadeSamples = 1024;    // ~21 ms at 48 kHz
    private const int MaxOsc = 3;
    private const double StructFadeSeconds = 0.010;
    private const float TwoPi = 6.2831855f;
    private const float PadChorusSemis = 0.03f;

    // sign of the MOD harmonic push per oscillator slot: +1 Yang, -1 Yin
    private static readonly float[] SignsDrone1 = [1f, 1f, 0f], SignsDrone2 = [1f, -1f, 0f], SignsPad1 = [1f, -1f, -1f],
        SignsPad2 = [1f, 1f, -1f], SignsAtmos1 = [1f, 0f, 0f];

    private readonly float _fs;
    private readonly WavetableOscillator[] _osc = new WavetableOscillator[MaxOsc], _oscOld = new WavetableOscillator[MaxOsc];
    private StateVariableFilter _filter;
    private ArEnvelope _env;
    private readonly VoiceModulator _vm;
    private Wavetable? _table;
    private float _velGain;
    private int _xfade, _xfadeTotal;
    private float _attackMs, _releaseMs;

    // structure / output stage state
    private Structure _structure;
    private bool _structLatch;                  // next chunk adopts the layer's structure without a fade
    private int _fade;                          // 0 none, -1 fading out for a structure switch, +1 fading in
    private float _sg = 1f;                     // structure-switch gain
    private readonly float _sgStep;
    private readonly float[] _gc = new float[MaxOsc], _gs = new float[MaxOsc], _gt = new float[MaxOsc];
    private readonly bool[] _act = new bool[MaxOsc];
    private bool _gInit;
    private double _mPhase;                     // ATMOS2 modulator phase (cycles)
    private float _fb1, _fb2;                   // ATMOS2 modulator feedback memory
    private float _beta, _fbk;                  // ATMOS2 current index / feedback (ramped per chunk)
    private float _curNote;                     // sounding pitch in MIDI notes (glides towards Note)
    private float _pl, _pr;
    private bool _panInit;
    private bool _notify;                       // tell the layer clocks about this note start

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

    // ---- Voice-mode contract (used by the layer voice modes) ----
    /// <summary>Glide time in ms for pitch moves made with <see cref="SlideTo"/> (0 = instant).</summary>
    public float GlideMs;
    /// <summary>
    /// Moves the sounding pitch to <paramref name="note"/> over <see cref="GlideMs"/> WITHOUT retriggering the envelope or
    /// the LFOs (legato / mono glide). The pitch approaches the target exponentially in the semitone (log-frequency)
    /// domain: about 95 % of the interval is covered after <see cref="GlideMs"/> (time constant GlideMs/3). Audio thread only.
    /// </summary>
    public void SlideTo(int note)
    {
        Note = note;
        if (GlideMs <= 0.5f) _curNote = note;
    }
    /// <summary>Static pitch offset of this voice in cents (unison detune spread).</summary>
    public float DetuneOffsetCents;
    /// <summary>Static stereo spread offset -1..1 for this voice (unison width), added to the layer pan (x63 pan units).</summary>
    public float PanSpread;

    /// <summary>Test hook: structure the voice currently renders.</summary>
    internal Structure CurrentStructure => _structure;
    /// <summary>Test hook: the pitch (MIDI note, fractional) the voice is currently sounding.</summary>
    internal float SoundingNote => _curNote;

    public SynthVoice(float sampleRate, int seed = 0)
    {
        _fs = sampleRate;
        _filter = new StateVariableFilter(sampleRate);
        _env = new ArEnvelope(sampleRate);
        _vm = new VoiceModulator(sampleRate, (uint)(seed + 1) * 7919u);
        _sgStep = (float)(1.0 / (StructFadeSeconds * sampleRate));
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

    private void BeginNote()
    {
        for (int k = 0; k < MaxOsc; k++) _osc[k].SetWave(_table!);
        _xfade = 0;
        _curNote = Note;
        _structLatch = true; _fade = 0; _sg = 1f;
        _gInit = false; _panInit = false;
        _mPhase = 0; _fb1 = _fb2 = 0f;
        _vm.Retrigger();
        _notify = true;
        _filter.Reset();
        _env.SetTimes(_attackMs, _releaseMs);
        _env.NoteOn();
    }

    /// <summary>Starts a note on a free voice.</summary>
    public void Start(Wavetable? table, int note, int velocity, long age)
    {
        _table = table;
        Note = note; Age = age;
        Active = true; Gate = true; SustainDeferred = false; Pending = false;
        _velGain = VelocityGain(velocity);
        BeginNote();
    }

    /// <summary>Same note again: re-opens the envelope from its current level (no click, no filter reset); the LFOs restart.</summary>
    public void Retrigger(int velocity, long age)
    {
        Age = age;
        Gate = true; SustainDeferred = false;
        if (Pending) { PendingVelocity = velocity; return; }
        _velGain = VelocityGain(velocity);
        _vm.Retrigger();
        _notify = true;
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
            for (int k = 0; k < MaxOsc; k++) _oscOld[k] = _osc[k];   // intentional struct copies: the old wave keeps playing from the same phase
            _xfade = _xfadeTotal = WaveFadeSamples;
        }
        else _xfade = 0;
        _table = table;
        for (int k = 0; k < MaxOsc; k++) _osc[k].SetWave(table!);
    }

    public void Kill()
    {
        _env.Kill();
        Gate = false; SustainDeferred = false; Pending = false;
    }

    private static double Hz(double midi) => 440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0);

    private readonly float[] _legacyL = new float[VoiceModulator.Cap], _legacyR = new float[VoiceModulator.Cap];

    /// <summary>
    /// LEGACY mono entry for layers that still build a block without <see cref="VoiceBlock.Mod"/> and apply the pan law
    /// themselves: renders one plain oscillator (neutral structure) and adds the mono sum. Remove once the layers pass
    /// <see cref="LayerModulation"/> and the stereo <see cref="Process(Span{float},Span{float},int,in VoiceBlock)"/>.
    /// </summary>
    public void Process(Span<float> mix, int n, in VoiceBlock b)
    {
        var nb = b;
        if (nb.Mod == null)
        {
            nb.Mod = LayerModulation.Neutral;
            nb.CutoffKnob = ParamCurves.CutoffToKnob(b.CutoffHz);
            nb.ResoKnob = 127f * MathF.Log(Math.Max(b.Q, 0.3f) / 0.3f) / MathF.Log(10f / 0.3f);
            nb.Pan = 0f;
        }
        int done = 0;
        while (done < n)
        {
            int c = Math.Min(VoiceModulator.Cap, n - done);
            nb.Harmonic0 = b.Harmonic0 + b.HarmonicStep * done;
            var l = _legacyL.AsSpan(0, c); var r = _legacyR.AsSpan(0, c);
            l.Clear(); r.Clear();
            Process(l, r, c, in nb);
            for (int i = 0; i < c; i++) mix[done + i] += (l[i] + r[i]) * 0.70710678f;   // undo the centred pan law
            done += c;
        }
    }

    /// <summary>Adds this voice's stereo output for <paramref name="n"/> samples into <paramref name="mixL"/> / <paramref name="mixR"/>.</summary>
    public void Process(Span<float> mixL, Span<float> mixR, int n, in VoiceBlock b)
    {
        if (Pending && !_env.IsActive)
        {
            int vel = PendingVelocity;
            Pending = false;
            _velGain = VelocityGain(vel);
            BeginNote();
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
            ProcessChunk(mixL.Slice(done, c), mixR.Slice(done, c), c, done, b.Harmonic0 + b.HarmonicStep * done, b.HarmonicStep, in b);
            done += c;
        }
        if (!_env.IsActive && !Pending) Active = false;
    }

    private void ProcessChunk(Span<float> mixL, Span<float> mixR, int n, int offset, float harm0, float harmStep, in VoiceBlock b)
    {
        var m = b.Mod;
        var vm = _vm;
        if (_notify) { _notify = false; m.NoteStarted(); }
        vm.Prepare(m, n, offset);

        // ---- structure latch / switch fade
        var target = m.Structure;
        if (_structLatch) { _structure = target; _structLatch = false; _fade = 0; _sg = 1f; _gInit = false; }
        else if (_fade == -1 && _sg <= 0f) { _structure = target; _fade = 1; _gInit = false; _fb1 = _fb2 = 0f; }
        else if (_fade == 0 && target != _structure) _fade = -1;

        // ---- pitch
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
        int mid = n >> 1;
        float baseS = _curNote + b.PitchSemis + DetuneOffsetCents * 0.01f + vm.Tune;
        float pitchUnits = m.PitchUnits + vm.Pitch;
        float semisP = ParamCurves.PitchSemitones(pitchUnits);
        float detKnob = Math.Clamp(m.DetuneKnob + vm.DtFb, 0f, 127f);
        float det = ParamCurves.DetuneCents(detKnob) * 0.01f;
        float balKnob = Math.Clamp(m.BalanceKnob + vm.Balance, 0f, 127f);
        float modMid = vm.ModD > 0f ? vm.Mod[mid] : 0f;

        // ---- filter
        _filter.Mode = b.Filter;
        if (b.Filter != FilterMode.Off)
        {
            float ck = Math.Clamp(b.CutoffKnob + vm.Cutoff, 0f, 127f), rk = Math.Clamp(b.ResoKnob + vm.Reso, 0f, 127f);
            float cHz = ParamCurves.CutoffHz(ck);
            if (b.Filter == FilterMode.BandPass) _filter.SetBandwidthOctaves(cHz, ParamCurves.BandpassOctaves(rk));
            else _filter.SetParameters(cHz, ParamCurves.ResonanceQ(rk));
        }

        // ---- pan (per voice: layer pan + unison spread + PAN LFO), ramped over the chunk
        float panKnob = Math.Clamp(b.Pan + PanSpread * 63f + vm.Pan, -63f, 63f);
        var (tpl, tpr) = ParamCurves.PanGains(panKnob);
        if (!_panInit) { _pl = tpl; _pr = tpr; _panInit = true; }
        _pls = (tpl - _pl) / n; _prs = (tpr - _pr) / n;

        float envGain = _velGain;
        float sgStep = _fade == 0 ? 0f : _sgStep;
        bool hasLvl = vm.HasLvl, hasOsc = vm.HasOscLvl;

        if (_structure == Structure.Atmos2)
        {
            double fc = Hz(baseS);
            float ratio = ParamCurves.Atmos2Ratio(pitchUnits);
            if (modMid != 0f) ratio *= MathF.Pow(2f, modMid * vm.ModD);
            double fm = fc * ratio;
            _osc[0].SetFrequency(fc, _fs);
            if (_xfade > 0) _oscOld[0].SetFrequency(fc, _fs);
            double mInc = fm / _fs;
            float beta = ParamCurves.Atmos2FmIndex(balKnob);
            float lim = Math.Max(0.05f, (float)((0.45 * _fs - fc) / Math.Max(fm, 1.0)) - 1f);
            float betaT = Math.Min(beta, lim);
            float fbT = ParamCurves.Atmos2Feedback(detKnob);
            if (!_gInit) { _beta = betaT; _fbk = fbT; _gInit = true; }
            float bs = (betaT - _beta) / n, fs2 = (fbT - _fbk) / n;
            if (fm > 0.45 * _fs) { bs = 0; _beta = 0.05f; betaT = 0.05f; }
            m.CountOscillators(2);

            for (int i = 0; i < n; i++)
            {
                _mPhase += mInc;
                if (_mPhase >= 1.0) _mPhase -= Math.Floor(_mPhase);
                float fbTerm = _fbk * 0.5f * (_fb1 + _fb2);
                float y = Lfo.FastSin((float)_mPhase + fbTerm * (1f / TwoPi));
                _fb2 = _fb1; _fb1 = y;
                float pm = _beta * y * (1f / TwoPi);
                _beta += bs; _fbk += fs2;

                float h = harm0 + harmStep * i + (vm.HasHarm ? vm.Harm[i] : 0f);
                _osc[0].SetPosition(h);
                float s = _osc[0].NextPm(pm);
                if (_xfade > 0)
                {
                    _oscOld[0].SetPosition(h);
                    float o = _oscOld[0].NextPm(pm);
                    s += (o - s) * ((float)_xfade-- / _xfadeTotal);
                }
                Finish(mixL, mixR, i, s, sgStep, hasOsc, hasLvl, envGain, vm);
            }
            _beta = betaT; _fbk = fbT;
        }
        else
        {
            int cnt = StructureInfo.OscillatorCount(_structure);
            float[] signs;
            var (ba, bb) = ParamCurves.BalanceGains(balKnob);
            float s0, s1 = 0f, s2 = 0f;      // semitone pitch of each slot
            const float R = 0.70710678f;
            switch (_structure)
            {
                case Structure.Drone1:
                    signs = SignsDrone1;
                    s0 = baseS; s1 = baseS + semisP + det + vm.ModD * vm.ModD * modMid;
                    _gt[0] = ba; _gt[1] = bb; _gt[2] = 0f;
                    break;
                case Structure.Drone2:
                    signs = SignsDrone2;
                    s0 = baseS; s1 = baseS + semisP + det;
                    _gt[0] = ba; _gt[1] = bb; _gt[2] = 0f;
                    break;
                case Structure.Pad1:      // balance: Yin (left) <-> Yang (right)
                    signs = SignsPad1;
                    s0 = baseS; s1 = baseS + semisP + det; s2 = baseS + semisP - det;
                    _gt[0] = bb; _gt[1] = ba * R; _gt[2] = ba * R;
                    break;
                case Structure.Pad2:      // balance: osc1,2 (left) <-> osc3 (right)
                    signs = SignsPad2;
                    s0 = baseS; s1 = baseS + PadChorusSemis; s2 = baseS + semisP + det;
                    _gt[0] = ba * R; _gt[1] = ba * R; _gt[2] = bb;
                    break;
                default:                  // Atmos1
                    signs = SignsAtmos1;
                    s0 = baseS + semisP + det;
                    _gt[0] = 1f; _gt[1] = 0f; _gt[2] = 0f;
                    break;
            }
            // loudness trim: coherent oscillators (same pitch / phase) add up, so more oscillators get a lower ceiling
            float nrm = _structure is Structure.Drone1 or Structure.Drone2 ? 0.85f : _structure is Structure.Pad1 or Structure.Pad2 ? 0.7f : 1f;
            _gt[0] *= nrm; _gt[1] *= nrm; _gt[2] *= nrm;
            double f0 = Hz(s0), f1 = Hz(s1), f2 = Hz(s2);
            int rendered = 0;
            for (int k = 0; k < cnt; k++)
            {
                if (!_gInit) _gc[k] = _gt[k];
                _gs[k] = (_gt[k] - _gc[k]) / n;
                _act[k] = _gc[k] != 0f || _gt[k] != 0f;
                if (!_act[k]) continue;
                rendered++;
                double f = k == 0 ? f0 : k == 1 ? f1 : f2;
                _osc[k].SetFrequency(f, _fs);
                if (_xfade > 0) _oscOld[k].SetFrequency(f, _fs);
            }
            _gInit = true;
            m.CountOscillators(rendered);

            bool ring = _structure == Structure.Atmos1 && vm.ModD > 0f;
            bool hmod = vm.ModD > 0f && _structure is Structure.Drone2 or Structure.Pad1 or Structure.Pad2;
            float hscale = 64f * vm.ModD, md = vm.ModD;
            float xfT = _xfade > 0 ? 1f / _xfadeTotal : 0f;

            for (int i = 0; i < n; i++)
            {
                float hb = harm0 + harmStep * i + (vm.HasHarm ? vm.Harm[i] : 0f);
                float mo = hmod ? hscale * vm.Mod[i] : 0f;
                float xt = _xfade > 0 ? _xfade * xfT : 0f;
                float s = 0f;
                for (int k = 0; k < cnt; k++)
                {
                    if (!_act[k]) continue;
                    float pos = hb + signs[k] * mo;
                    _osc[k].SetPosition(pos);
                    float o = _osc[k].Next();
                    if (xt > 0f)
                    {
                        _oscOld[k].SetPosition(pos);
                        o += (_oscOld[k].Next() - o) * xt;
                    }
                    s += o * _gc[k];
                    _gc[k] += _gs[k];
                }
                if (_xfade > 0) _xfade--;
                if (ring) s *= 1f - md + md * vm.Mod[i];
                Finish(mixL, mixR, i, s, sgStep, hasOsc, hasLvl, envGain, vm);
            }
            for (int k = 0; k < cnt; k++) _gc[k] = _gt[k];
        }

        _pl = tpl; _pr = tpr;
        if (_fade == 1 && _sg >= 1f) _fade = 0;
    }

    // ---- shared per-sample tail: structure fade -> OSC LEVEL -> filter -> envelope -> LEVEL -> pan
    private void Finish(Span<float> mixL, Span<float> mixR, int i, float s, float sgStep, bool hasOsc, bool hasLvl,
        float envGain, VoiceModulator vm)
    {
        if (sgStep != 0f)
        {
            if (_fade == -1) { _sg -= sgStep; if (_sg < 0f) _sg = 0f; }
            else if (_fade == 1) { _sg += sgStep; if (_sg > 1f) _sg = 1f; }
            s *= _sg;
        }
        if (hasOsc) s *= vm.OscLvl[i];
        s = _filter.Process(s);
        float o = s * _env.Next() * envGain;
        if (hasLvl) o *= vm.Lvl[i];
        mixL[i] += o * _pl;
        mixR[i] += o * _pr;
        _pl += _pls; _pr += _prs;
    }

    private float _pls, _prs;
}
