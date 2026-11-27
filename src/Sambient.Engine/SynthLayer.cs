using Sambient.Dsp.Filters;
using Sambient.Dsp.Params;
using Sambient.Dsp.Wavetables;

namespace Sambient.Engine;

/// <summary>
/// A 16-voice POLY synth layer. All methods run on the audio thread only (events arrive via the engine's queue).
///
/// Voice allocation: same note again reuses its voice (envelope re-opens from its current level); otherwise a free voice;
/// otherwise the oldest releasing voice is stolen, otherwise the oldest held voice. A stolen voice is killed with a ~5 ms
/// fade and then starts the new note on the same voice (simple, click-free; adds up to ~5 ms latency to stolen notes).
///
/// Sustain pedal (M1 HOLD): note-offs are deferred while the pedal is down and executed at pedal-up.
///
/// WAVE change: the audio side only swaps when the bank reports the wave built (the UI/param setter requested the build);
/// until then the old wave keeps playing. Swapping crossfades old -> new in every active voice (~21 ms).
///
/// Mod wheel (TEMPORARY, until LFO/MOD exist in later milestones): adds 0..+48 to HARMONIC (clamped to 127).
/// </summary>
public sealed class SynthLayer : ILayer, IVoiceSink, IModulatedLayer
{
    public const int VoiceCount = 16;
    public const int SubBlock = 32;
    public const float ModWheelHarmonicRange = 48f;
    /// <summary>Fixed headroom so a few simultaneous voices do not hit the safety clipper (about -6 dB).</summary>
    public const float VoiceMixScale = 0.5f;

    private readonly int _index;
    private readonly float _fs;
    private readonly ParameterStore _params;
    private readonly WavetableBank _bank;
    private readonly SynthVoice[] _voices = new SynthVoice[VoiceCount];
    private readonly float[] _mix = new float[SubBlock], _mixR = new float[SubBlock];
    private readonly LayerModulation _mod;
    private bool _longRelease;

    private SmoothedValue _harm, _cutoff, _reso, _level, _pan, _bend;
    private Wavetable? _table;
    private float _attackKnob = -1, _releaseKnob = -1;
    private long _age;
    private bool _sustain;
    private float _modWheel;
    private float _bendTarget;
    private readonly VoiceModeController _ctl;

    // per-block cached parameter values
    private FilterMode _filter;
    private float _octaveSemis, _bendRange;
    private readonly Tuning _tuning = new(semitoneOnly: false);
    private SmoothedValue _tuneAll;      // TUNE ALL in cents (smoothed, no zipper)
    private SmoothedValue _octave;       // OCTAVE in semitones: a 5 ms phase-continuous ramp instead of an instant pitch step
    private float _gPrev = -1f;        // output gain at the end of the previous sub-block (-1 = none yet)
    private SmoothedValue _unisonGain;   // 0.5 in UNISON (4 voices per note), 1 otherwise: smoothed so a mode change never steps the level

    public int Index => _index;
    public int ActiveVoices { get; private set; }
    internal SynthVoice[] VoicesForTest => _voices;
    /// <summary>Layer-wide structure / MOD / LFO state (also read by the engine for the LFO send modulation).</summary>
    internal LayerModulation Mod => _mod;
    LayerModulation IModulatedLayer.Mod => _mod;
    public Wavetable? CurrentTable => _table;

    public SynthLayer(int index, float sampleRate, ParameterStore parameters, WavetableBank bank)
    {
        _index = index; _fs = sampleRate; _params = parameters; _bank = bank;
        _ctl = new VoiceModeController(parameters, index, sampleRate, this, unisonSupported: true);
        _mod = new LayerModulation(index, sampleRate, parameters);
        for (int i = 0; i < VoiceCount; i++) { _voices[i] = new SynthVoice(sampleRate, index * VoiceCount + i); _voices[i].Tuning = _tuning; }
        _harm = new SmoothedValue(parameters.Get(index, ParamId.Harmonic), sampleRate, 15f);
        _cutoff = new SmoothedValue(parameters.Get(index, ParamId.Cutoff), sampleRate, 15f);
        _reso = new SmoothedValue(parameters.Get(index, ParamId.Resonance), sampleRate, 15f);
        _level = new SmoothedValue(parameters.Get(index, ParamId.Level), sampleRate, 20f);
        _pan = new SmoothedValue(parameters.Get(index, ParamId.Pan), sampleRate, 20f);
        _bend = new SmoothedValue(0f, sampleRate, 8f);
        _tuneAll = new SmoothedValue(parameters.Get(GlobalParamId.TuneAllCents), sampleRate, 25f);
        _octave = new SmoothedValue(12f * parameters.Get(index, ParamId.Octave), sampleRate, 5f);
        _unisonGain = new SmoothedValue(_ctl.Mode == VoiceMode.Unison ? UnisonGain : 1f, sampleRate, 20f);
        UpdateParams();
    }

    // ---------------------------------------------------------------- events
    // Note logic (voice modes, HOLD, sustain bookkeeping for the single-voice modes) lives in VoiceModeController; the
    // IVoiceSink methods below do the actual voice work.

    public void NoteOn(int note, int velocity)
    {
        if (velocity <= 0) velocity = (int)MathF.Round(_params.Get(_index, ParamId.Velocity));
        _ctl.NoteOn(note, velocity);
    }

    public void NoteOff(int note) => _ctl.NoteOff(note);

    /// <summary>HOLD: latch the key, or release it when it is already latched.</summary>
    public void HoldToggle(int note, int velocity)
    {
        if (velocity <= 0) velocity = (int)MathF.Round(_params.Get(_index, ParamId.Velocity));
        _ctl.HoldToggle(note, velocity);
    }

    /// <summary>Releases all latched (HOLD) keys of this layer.</summary>
    public void ClearHolds() => _ctl.ClearHolds();

    public void AllNotesOff() => _ctl.AllNotesOff();

    /// <summary>Test/diagnostic: the note logic of this layer.</summary>
    public VoiceModeController Modes => _ctl;

    void IVoiceSink.ReleaseAll()
    {
        foreach (var v in _voices)
            if (v.Active && (v.Gate || v.SustainDeferred)) v.Release();
    }

    void IVoiceSink.PolyOn(int note, int velocity)
    {
        if (_ctl.Mode == VoiceMode.Unison) { UnisonOn(note, velocity); return; }
        long age = ++_age;

        SynthVoice v = null!;
        bool found = false;
        foreach (var c in _voices)
            if (c.Active && c.Note == note) { v = c; found = true; break; }
        if (found) { Plain(v); v.Retrigger(velocity, age); return; }

        foreach (var c in _voices)
            if (!c.Active) { v = c; found = true; break; }
        if (found) { Plain(v); v.Start(_table, note, velocity, age); return; }

        // steal: oldest releasing, else oldest held (never a voice that is already waiting for a steal if avoidable)
        SynthVoice? best = null;
        foreach (var c in _voices)
            if (c.IsReleasing && (best == null || c.Age < best.Age)) best = c;
        if (best == null)
            foreach (var c in _voices)
                if (!c.Pending && (best == null || c.Age < best.Age)) best = c;
        if (best == null)
            foreach (var c in _voices)
                if (best == null || c.Age < best.Age) best = c;
        Plain(best!);
        best!.Steal(note, velocity, age);
    }

    /// <summary>Clears unison / glide leftovers so a plain voice sounds normal.</summary>
    private static void Plain(SynthVoice v) { v.DetuneOffsetCents = 0f; v.PanSpread = 0f; v.GlideMs = 0f; }

    void IVoiceSink.PolyOff(int note)
    {
        foreach (var v in _voices)
        {
            if (!v.Active || v.Note != note || !v.Gate || v.SustainDeferred) continue;
            if (_sustain) v.SustainDeferred = true;
            else v.Release();
        }
    }

    void IVoiceSink.MonoStart(int note, int velocity, float glideMs, bool retrigger)
    {
        var v = _voices[0];
        long age = ++_age;
        v.DetuneOffsetCents = 0f; v.PanSpread = 0f;
        v.GlideMs = glideMs;
        if (v.Active && !v.Pending)
        {
            v.SlideTo(note);                       // GlideMs 0 = instant
            if (retrigger || !v.Gate) v.Retrigger(velocity, age);
        }
        else if (v.Active) { v.SlideTo(note); v.Retrigger(velocity, age); }
        else v.Start(_table, note, velocity, age);
    }

    void IVoiceSink.MonoStop()
    {
        var v = _voices[0];
        if (v.Active && (v.Gate || v.SustainDeferred)) v.Release();
    }

    // ---- UNISON: 4 voices per note, groups of 4 (voices g*4 .. g*4+3)
    private const int UnisonVoices = 4;
    private const int UnisonGroups = VoiceCount / UnisonVoices;
    private static readonly float[] UnisonDetuneShape = { -1f, -1f / 3f, 1f / 3f, 1f };
    private static readonly float[] UnisonPan = { -0.6f, -0.2f, 0.2f, 0.6f };
    /// <summary>Output compensation while in UNISON (4 voices per note).</summary>
    public const float UnisonGain = 0.5f;

    private bool GroupHasNote(int g, int note)
    {
        for (int k = 0; k < UnisonVoices; k++)
        {
            var v = _voices[g * UnisonVoices + k];
            if (!v.Active || v.Note != note) return false;
        }
        return true;
    }

    private void UnisonOn(int note, int velocity)
    {
        long age = ++_age;
        float cents = ParamCurves.UnisonDetuneCents(_params.Get(_index, ParamId.UnisonDetune));
        int target = -1;
        for (int g = 0; g < UnisonGroups && target < 0; g++) if (GroupHasNote(g, note)) target = g;
        if (target >= 0)
        {
            for (int k = 0; k < UnisonVoices; k++)
            {
                var v = _voices[target * UnisonVoices + k];
                v.DetuneOffsetCents = cents * UnisonDetuneShape[k]; v.PanSpread = UnisonPan[k];
                v.Retrigger(velocity, age);
            }
            return;
        }
        for (int g = 0; g < UnisonGroups && target < 0; g++)
        {
            bool free = true;
            for (int k = 0; k < UnisonVoices; k++) if (_voices[g * UnisonVoices + k].Active) free = false;
            if (free) target = g;
        }
        bool steal = target < 0;
        if (steal)
        {
            // oldest releasing group, else oldest group (never one that is still waiting for a steal if avoidable)
            int bestReleasing = -1, bestAny = -1;
            for (int g = 0; g < UnisonGroups; g++)
            {
                bool releasing = true, pending = false;
                for (int k = 0; k < UnisonVoices; k++)
                {
                    var v = _voices[g * UnisonVoices + k];
                    if (v.Active && !v.IsReleasing) releasing = false;
                    if (v.Pending) pending = true;
                }
                long ga = _voices[g * UnisonVoices].Age;
                if (releasing && (bestReleasing < 0 || ga < _voices[bestReleasing * UnisonVoices].Age)) bestReleasing = g;
                if (!pending && (bestAny < 0 || ga < _voices[bestAny * UnisonVoices].Age)) bestAny = g;
            }
            target = bestReleasing >= 0 ? bestReleasing : bestAny >= 0 ? bestAny : 0;
        }
        for (int k = 0; k < UnisonVoices; k++)
        {
            var v = _voices[target * UnisonVoices + k];
            v.DetuneOffsetCents = cents * UnisonDetuneShape[k]; v.PanSpread = UnisonPan[k]; v.GlideMs = 0f;
            if (v.Active) v.Steal(note, velocity, age);
            else v.Start(_table, note, velocity, age);
        }
    }

    public void SetSustain(bool down)
    {
        _sustain = down;
        _ctl.SetSustain(down);
        if (down) return;
        foreach (var v in _voices)
            if (v.Active && v.SustainDeferred) v.Release();
    }

    public void SetPitchBend(float value) => _bendTarget = Math.Clamp(value, -1f, 1f);
    public void SetModWheel(float value) => _modWheel = Math.Clamp(value, 0f, 1f);

    // ---------------------------------------------------------------- rendering

    private void UpdateParams()
    {
        int i = _index;
        _mod.Update();
        // WAVE: swap only when the requested wave is built; otherwise keep playing the old one.
        int wave = (int)MathF.Round(_params.Get(i, ParamId.Wave));
        if (_bank.IsReady(wave))
        {
            var t = _bank.Get(wave);
            if (t != null && !ReferenceEquals(t, _table))
            {
                _table = t;
                foreach (var v in _voices) v.SwapWave(t);
            }
        }
        else if (_table == null)
        {
            // nothing playing yet and requested wave not ready: use whatever fallback is built (e.g. SIN.S)
            var t = _bank.Get(wave);
            if (t != null) _table = t;
        }

        float harm = _params.Get(i, ParamId.Harmonic) + _modWheel * ModWheelHarmonicRange;
        _harm.SetTarget(Math.Clamp(harm, 0f, 127f));
        _cutoff.SetTarget(_params.Get(i, ParamId.Cutoff));
        _reso.SetTarget(_params.Get(i, ParamId.Resonance));
        _level.SetTarget(_params.Get(i, ParamId.Level));
        _pan.SetTarget(_params.Get(i, ParamId.Pan));
        _bend.SetTarget(_bendTarget);
        _filter = (FilterType)(int)_params.Get(i, ParamId.FilterType) switch
        {
            FilterType.LowPass => FilterMode.LowPass,
            FilterType.HighPass => FilterMode.HighPass,
            FilterType.BandPass => FilterMode.BandPass,
            _ => FilterMode.Off,
        };
        // Tuning: table per layer (temperament + TuningKey + master tune); SLFG/CHKR/PLNT ignore master tune and OCTAVE.
        _tuning.Update((Temperament)(int)_params.Get(GlobalParamId.Temperament), (int)_params.Get(i, ParamId.TuningKey),
            _params.Get(GlobalParamId.MasterTuneCents));
        _tuneAll.SetTarget(_params.Get(GlobalParamId.TuneAllCents));
        _unisonGain.SetTarget(_ctl.Mode == VoiceMode.Unison ? UnisonGain : 1f);
        _octaveSemis = _tuning.IsSpecial ? 0f : 12f * _params.Get(i, ParamId.Octave);
        _octave.SetTarget(_octaveSemis);
        _bendRange = _params.Get(GlobalParamId.PitchBendRange);

        float a = _params.Get(i, ParamId.Attack), r = _params.Get(i, ParamId.Release);
        bool lr = _mod.LongRelease;     // DRONE structures: release range 0..20 s
        if (a != _attackKnob || r != _releaseKnob || lr != _longRelease)
        {
            _attackKnob = a; _releaseKnob = r; _longRelease = lr;
            float am = ParamCurves.AttackMs(a), rm = ParamCurves.ReleaseMs(r, lr);
            foreach (var v in _voices) v.SetTimes(am, rm);
        }
    }

    /// <summary>Renders <paramref name="frames"/> frames and ADDS them to the interleaved stereo buffer.</summary>
    public void Render(Span<float> interleavedStereo, int frames)
    {
        _ctl.Sync();
        UpdateParams();
        int offset = 0;
        while (offset < frames)
        {
            _ctl.ProcessArpEvents();
            int n = Math.Min(Math.Min(SubBlock, frames - offset), Math.Max(1, _ctl.SamplesToNextArpEvent()));
            RenderSub(interleavedStereo, offset, n);
            _ctl.AdvanceArp(n);
            offset += n;
        }
        int active = 0;
        foreach (var v in _voices) if (v.Active) active++;
        ActiveVoices = active;
    }

    private void RenderSub(Span<float> output, int offset, int n)
    {
        float h0 = _harm.Current;
        _harm.Skip(n);
        float h1 = _harm.Current;
        _cutoff.Skip(n); _reso.Skip(n); _level.Skip(n); _pan.Skip(n); _bend.Skip(n); _tuneAll.Skip(n); _octave.Skip(n); _unisonGain.Skip(n);
        _mod.BeginSub(n);       // layer-level LFO clocks (free-running even without voices) and send modulation

        // output gain: ramped per sample from the previous sub-block's value (the LEVEL curve is dB-linear, so a knob step of a
        // few units is a gain step of up to 2 dB: constant per sub-block it would zipper / click)
        float gNew = ParamCurves.LevelGain(_level.Current) * VoiceMixScale * _unisonGain.Current;
        float g0 = _gPrev < 0f ? gNew : _gPrev;
        _gPrev = gNew;

        bool any = false;
        foreach (var v in _voices) if (v.Active) { any = true; break; }
        if (!any) return;

        var block = new VoiceBlock
        {
            Harmonic0 = h0,
            HarmonicStep = (h1 - h0) / n,
            PitchSemis = _octave.Current + _bend.Current * _bendRange + _tuneAll.Current * 0.01f,
            Filter = _filter,
            CutoffHz = ParamCurves.CutoffHz(_cutoff.Current),
            Q = ParamCurves.ResonanceQ(_reso.Current),
            BandwidthOct = ParamCurves.BandpassOctaves(_reso.Current),
            CutoffKnob = _cutoff.Current,
            ResoKnob = _reso.Current,
            Pan = _pan.Current,
            Mod = _mod,
        };

        // voices apply the pan law themselves (layer pan + unison spread + PAN LFO) and add into the stereo mix
        var mix = _mix.AsSpan(0, n);
        var mixR = _mixR.AsSpan(0, n);
        mix.Clear(); mixR.Clear();
        foreach (var v in _voices)
            if (v.Active) v.Process(mix, mixR, n, in block);

        float gs = (gNew - g0) / n, g = g0;
        int o = offset * 2;
        for (int i = 0; i < n; i++)
        {
            g += gs;
            output[o++] += mix[i] * g;
            output[o++] += mixR[i] * g;
        }
    }

    /// <summary>Test/diagnostic: number of voices currently allocated for <paramref name="note"/> (gate on or off).</summary>
    public int VoicesForNote(int note)
    {
        int c = 0;
        foreach (var v in _voices) if (v.Active && v.Note == note) c++;
        return c;
    }
}
