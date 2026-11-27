using Sambient.Dsp.Filters;
using Sambient.Dsp.Noise;
using Sambient.Dsp.Params;

namespace Sambient.Engine;

/// <summary>
/// Layer 4: the NOISE / sampler layer, 8 voices. Each voice mixes a stereo <see cref="SamplePlayer"/> with white noise;
/// HARMONIC is the balance (0 = white noise only, 63.5 = equal-power centre, 127 = sample only). WAVE selects the sample
/// slot 1..8 (built in the background by the <see cref="NoiseBank"/>; until it is ready only the white noise sounds),
/// <see cref="ParamId.NoisePlayMode"/> selects LOOP / SHOT. Then the same SVF + AR + level + pan as the synth layers.
///
/// Pitch: MIDI note 60 (C4) plays the sample at its original pitch, other keys transpose
/// (see <see cref="PlaybackRate"/>); white noise is not pitched.
/// TODO(LN.IN milestone): line input as an alternative source instead of the sample.
/// </summary>
public sealed class NoiseLayer : ILayer
{
    public const int VoiceCount = 8;
    public const int SubBlock = 32;
    public const int RootNote = 60;
    public const float VoiceMixScale = 0.5f;

    private readonly int _index;
    private readonly float _fs;
    private readonly ParameterStore _params;
    private readonly NoiseBank _bank;
    private readonly NoiseVoice[] _voices = new NoiseVoice[VoiceCount];
    private readonly float[] _mixL = new float[SubBlock];
    private readonly float[] _mixR = new float[SubBlock];

    private SmoothedValue _balance, _cutoff, _reso, _level, _pan, _bend;
    private NoiseSample? _sample;
    private float _attackKnob = -1, _releaseKnob = -1;
    private long _age;
    private bool _sustain;
    private float _bendTarget;
    private SamplePlayMode _mode = SamplePlayMode.Loop;

    private FilterMode _filter;
    private float _octaveSemis, _bendRange;

    public int Index => _index;
    public int ActiveVoices { get; private set; }
    public NoiseSample? CurrentSample => _sample;

    public NoiseLayer(int index, float sampleRate, ParameterStore parameters, NoiseBank bank)
    {
        _index = index; _fs = sampleRate; _params = parameters; _bank = bank;
        for (int i = 0; i < VoiceCount; i++) _voices[i] = new NoiseVoice(sampleRate, i);
        _balance = new SmoothedValue(parameters.Get(index, ParamId.Harmonic), sampleRate, 15f);
        _cutoff = new SmoothedValue(parameters.Get(index, ParamId.Cutoff), sampleRate, 15f);
        _reso = new SmoothedValue(parameters.Get(index, ParamId.Resonance), sampleRate, 15f);
        _level = new SmoothedValue(parameters.Get(index, ParamId.Level), sampleRate, 20f);
        _pan = new SmoothedValue(parameters.Get(index, ParamId.Pan), sampleRate, 20f);
        _bend = new SmoothedValue(0f, sampleRate, 8f);
        UpdateParams();
    }

    /// <summary>
    /// Playback ratio relative to the sample's ORIGINAL pitch including sample-rate conversion:
    /// <c>sample.SampleRate / outputRate × 2^(semitones/12)</c>, <paramref name="srcRatio"/> = sample.SampleRate / outputRate,
    /// <paramref name="semitones"/> = note − 60 + octave×12 + bend.
    /// </summary>
    public static double PlaybackRate(double srcRatio, double semitones) => srcRatio * Math.Pow(2.0, semitones / 12.0);

    /// <summary>Same as <see cref="PlaybackRate(double,double)"/> from raw sample rates.</summary>
    public static double PlaybackRate(int sampleRate, int outputRate, double semitones)
        => PlaybackRate((double)sampleRate / outputRate, semitones);

    // ---------------------------------------------------------------- events

    public void NoteOn(int note, int velocity)
    {
        if (velocity <= 0) velocity = (int)MathF.Round(_params.Get(_index, ParamId.Velocity));
        long age = ++_age;

        NoiseVoice? v = null;
        foreach (var c in _voices)
            if (c.Active && c.Note == note) { v = c; break; }
        if (v != null) { v.Retrigger(velocity, age); return; }

        foreach (var c in _voices)
            if (!c.Active) { v = c; break; }
        if (v != null) { v.Start(_sample, note, velocity, age); return; }

        NoiseVoice? best = null;
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

    /// <summary>The mod wheel has no target on this layer yet (TODO with the LFO/MOD milestone).</summary>
    public void SetModWheel(float value) { }

    // ---------------------------------------------------------------- rendering

    private void UpdateParams()
    {
        int i = _index;
        int slot = Math.Clamp((int)MathF.Round(_params.Get(i, ParamId.Wave)), 1, NoiseBank.SlotCount);
        if (_bank.IsReady(slot))
        {
            var s = _bank.Get(slot);
            if (s != null && !ReferenceEquals(s, _sample))
            {
                _sample = s;
                foreach (var v in _voices) v.SwapSample(s);
            }
        }

        var mode = _params.Get(i, ParamId.NoisePlayMode) >= 0.5f ? SamplePlayMode.OneShot : SamplePlayMode.Loop;
        if (mode != _mode)
        {
            _mode = mode;
            foreach (var v in _voices) v.SetMode(mode);
        }

        _balance.SetTarget(_params.Get(i, ParamId.Harmonic));
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
        _balance.Skip(n); _cutoff.Skip(n); _reso.Skip(n); _level.Skip(n); _pan.Skip(n); _bend.Skip(n);

        bool any = false;
        foreach (var v in _voices) if (v.Active) { any = true; break; }
        if (!any) return;

        // HARMONIC = balance: 0 noise only, 63.5 equal power centre, 127 sample only
        float t = Math.Clamp(_balance.Current, 0f, 127f) * (1f / 127f) * (MathF.PI * 0.5f);
        var block = new NoiseBlock
        {
            PitchSemis = _octaveSemis + _bend.Current * _bendRange,
            SrcRatio = _sample != null ? _sample.SampleRate / _fs : 1.0,
            NoiseGain = MathF.Cos(t),
            SampleGain = MathF.Sin(t),
            Filter = _filter,
            CutoffHz = ParamCurves.CutoffHz(_cutoff.Current),
            Q = ParamCurves.ResonanceQ(_reso.Current),
            BandwidthOct = ParamCurves.BandpassOctaves(_reso.Current),
        };

        var mixL = _mixL.AsSpan(0, n);
        var mixR = _mixR.AsSpan(0, n);
        mixL.Clear(); mixR.Clear();
        foreach (var v in _voices)
            if (v.Active) v.Process(mixL, mixR, n, in block);

        float g = ParamCurves.LevelGain(_level.Current) * VoiceMixScale;
        var (pl, pr) = ParamCurves.PanGains(_pan.Current);
        float gl = g * pl, gr = g * pr;
        int o = offset * 2;
        for (int i = 0; i < n; i++)
        {
            output[o++] += mixL[i] * gl;
            output[o++] += mixR[i] * gr;
        }
    }

    /// <summary>Test/diagnostic: voices currently allocated for <paramref name="note"/>.</summary>
    public int VoicesForNote(int note)
    {
        int c = 0;
        foreach (var v in _voices) if (v.Active && v.Note == note) c++;
        return c;
    }
}
