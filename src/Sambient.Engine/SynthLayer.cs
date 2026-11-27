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
public sealed class SynthLayer : ILayer
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
    private readonly float[] _mix = new float[SubBlock];

    private SmoothedValue _harm, _cutoff, _reso, _level, _pan, _bend;
    private Wavetable? _table;
    private float _attackKnob = -1, _releaseKnob = -1;
    private long _age;
    private bool _sustain;
    private float _modWheel;
    private float _bendTarget;

    // per-block cached parameter values
    private FilterMode _filter;
    private float _octaveSemis, _bendRange;

    public int Index => _index;
    public int ActiveVoices { get; private set; }
    public Wavetable? CurrentTable => _table;

    public SynthLayer(int index, float sampleRate, ParameterStore parameters, WavetableBank bank)
    {
        _index = index; _fs = sampleRate; _params = parameters; _bank = bank;
        for (int i = 0; i < VoiceCount; i++) _voices[i] = new SynthVoice(sampleRate);
        _harm = new SmoothedValue(parameters.Get(index, ParamId.Harmonic), sampleRate, 15f);
        _cutoff = new SmoothedValue(parameters.Get(index, ParamId.Cutoff), sampleRate, 15f);
        _reso = new SmoothedValue(parameters.Get(index, ParamId.Resonance), sampleRate, 15f);
        _level = new SmoothedValue(parameters.Get(index, ParamId.Level), sampleRate, 20f);
        _pan = new SmoothedValue(parameters.Get(index, ParamId.Pan), sampleRate, 20f);
        _bend = new SmoothedValue(0f, sampleRate, 8f);
        UpdateParams();
    }

    // ---------------------------------------------------------------- events

    public void NoteOn(int note, int velocity)
    {
        if (velocity <= 0) velocity = (int)MathF.Round(_params.Get(_index, ParamId.Velocity));
        long age = ++_age;

        SynthVoice? v = null;
        foreach (var c in _voices)
            if (c.Active && c.Note == note) { v = c; break; }
        if (v != null) { v.Retrigger(velocity, age); return; }

        foreach (var c in _voices)
            if (!c.Active) { v = c; break; }
        if (v != null) { v.Start(_table, note, velocity, age); return; }

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
        best!.Steal(note, velocity, age);
    }

    public void NoteOff(int note)
    {
        foreach (var v in _voices)
        {
            if (!v.Active || v.Note != note || !v.Gate || v.SustainDeferred) continue;
            if (_sustain) v.SustainDeferred = true;
            else v.Release();
        }
    }

    public void AllNotesOff()
    {
        foreach (var v in _voices)
            if (v.Active && (v.Gate || v.SustainDeferred)) v.Release();
    }

    public void SetSustain(bool down)
    {
        _sustain = down;
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
        _octaveSemis = 12f * _params.Get(i, ParamId.Octave);
        _bendRange = _params.Get(GlobalParamId.PitchBendRange);

        float a = _params.Get(i, ParamId.Attack), r = _params.Get(i, ParamId.Release);
        if (a != _attackKnob || r != _releaseKnob)
        {
            _attackKnob = a; _releaseKnob = r;
            float am = ParamCurves.AttackMs(a), rm = ParamCurves.ReleaseMs(r);
            foreach (var v in _voices) v.SetTimes(am, rm);
        }
    }

    /// <summary>Renders <paramref name="frames"/> frames and ADDS them to the interleaved stereo buffer.</summary>
    public void Render(Span<float> interleavedStereo, int frames)
    {
        UpdateParams();
        int offset = 0;
        while (offset < frames)
        {
            int n = Math.Min(SubBlock, frames - offset);
            RenderSub(interleavedStereo, offset, n);
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
        _cutoff.Skip(n); _reso.Skip(n); _level.Skip(n); _pan.Skip(n); _bend.Skip(n);

        bool any = false;
        foreach (var v in _voices) if (v.Active) { any = true; break; }
        if (!any) return;

        var block = new VoiceBlock
        {
            Harmonic0 = h0,
            HarmonicStep = (h1 - h0) / n,
            PitchSemis = _octaveSemis + _bend.Current * _bendRange,
            Filter = _filter,
            CutoffHz = ParamCurves.CutoffHz(_cutoff.Current),
            Q = ParamCurves.ResonanceQ(_reso.Current),
            BandwidthOct = ParamCurves.BandpassOctaves(_reso.Current),
        };

        var mix = _mix.AsSpan(0, n);
        mix.Clear();
        foreach (var v in _voices)
            if (v.Active) v.Process(mix, n, in block);

        float g = ParamCurves.LevelGain(_level.Current) * VoiceMixScale;
        var (pl, pr) = ParamCurves.PanGains(_pan.Current);
        float gl = g * pl, gr = g * pr;
        int o = offset * 2;
        for (int i = 0; i < n; i++)
        {
            float s = mix[i];
            output[o++] += s * gl;
            output[o++] += s * gr;
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
