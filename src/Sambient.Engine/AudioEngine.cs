using Sambient.Audio;
using Sambient.Dsp.Effects;
using Sambient.Dsp.Noise;
using Sambient.Dsp.Params;
using Sambient.Dsp.Wavetables;

namespace Sambient.Engine;

/// <summary>
/// The M2 engine: ParameterStore + lock-free event queue + 4 layers (3 <see cref="SynthLayer"/> + the
/// <see cref="NoiseLayer"/>), a mixer, a shimmer reverb on send buses, a master FX insert, master volume and a safety
/// soft clipper. <see cref="Render"/> never allocates, locks or blocks. All public setters are safe from any thread.
///
/// Signal flow per block: every layer renders stereo (after its own level/pan) into a scratch buffer; mute gain, then
/// (a) the dry bus, (b) the reverb-send bus x ReverbSend and (c) the shimmer-send bus x ShimmerSend (sends are post-level,
/// post-pan) -> ShimmerReverb.Process(send buses) -> wet; mix = dry + wet -> MasterFx.Process (master insert, after the
/// reverb) -> master volume (smoothed) -> soft clipper.
///
/// To change the sample rate create a new engine (non-RT) and share the ParameterStore / WavetableBank / NoiseBank with it;
/// effect instances are created per engine.
/// </summary>
public sealed class AudioEngine : IAudioCallback, ISequencerHost
{
    public const int SynthLayerCount = 3;
    public const int NoiseLayerIndex = ParameterStore.NoiseLayerIndex;
    public const int LayerCount = 4;
    /// <summary>Largest block processed in one go (longer callbacks are split); all bus buffers are sized for it.</summary>
    public const int MaxBlock = 4096;
    /// <summary>Soft clipper knee (-3 dBFS); output never exceeds 1.0.</summary>
    public const float ClipKnee = 0.7079458f;
    private const float PeakDecayPerBlock = 0.9f;

    private readonly ILayer[] _layers = new ILayer[LayerCount];
    private readonly SynthLayer[] _synthLayers = new SynthLayer[SynthLayerCount];
    private readonly NoiseLayer _noiseLayer;
    private readonly ShimmerReverb _reverb;
    private readonly MasterFx _fx;
    private SmoothedValue _master;
    private SmoothedValue _patternLevel;   // PTN LVL (pattern palette level), see FxRouting for its position
    private readonly SmoothedValue[] _mute = new SmoothedValue[LayerCount];
    private readonly SmoothedValue[] _revSend = new SmoothedValue[LayerCount];
    private readonly SmoothedValue[] _shimSend = new SmoothedValue[LayerCount];

    // preallocated per-block buffers (max block)
    private readonly float[] _layerBuf = new float[MaxBlock * 2];
    private readonly float[] _dryL = new float[MaxBlock], _dryR = new float[MaxBlock];
    private readonly float[] _revInL = new float[MaxBlock], _revInR = new float[MaxBlock];
    private readonly float[] _shimInL = new float[MaxBlock], _shimInR = new float[MaxBlock];
    private readonly float[] _wetL = new float[MaxBlock], _wetR = new float[MaxBlock];

    // last applied global effect settings (apply only on change)
    private int _lastReverbType = -1, _lastFxType = -1;
    private float _lastReverbMix = float.NaN, _lastFxSpeed = float.NaN, _lastFxAmount = float.NaN, _lastBpm = float.NaN;

    private IEngineSequencer? _seq;

    private float _peakL, _peakR;                 // audio thread running peaks (with decay)
    private float _pubPeakL, _pubPeakR;           // published (Volatile)
    private int _pubVoices;

    public int SampleRate { get; }
    public ParameterStore Parameters { get; }
    public WavetableBank Bank { get; }
    public NoiseBank Noise { get; }
    public EngineEventQueue Events { get; }

    public AudioEngine(int sampleRate, ParameterStore? parameters = null, WavetableBank? bank = null, NoiseBank? noise = null)
    {
        SampleRate = sampleRate;
        Parameters = parameters ?? new ParameterStore();
        Bank = bank ?? new WavetableBank();
        Noise = noise ?? new NoiseBank();
        Events = new EngineEventQueue(1024);
        for (int i = 0; i < SynthLayerCount; i++)
        {
            Bank.EnsureBuiltAsync((int)Parameters.Get(i, ParamId.Wave));
            _synthLayers[i] = new SynthLayer(i, sampleRate, Parameters, Bank);
            _layers[i] = _synthLayers[i];
        }
        Bank.EnsureBuiltAsync(WavetableBank.FallbackIndex);
        Noise.EnsureBuiltAsync((int)Parameters.Get(NoiseLayerIndex, ParamId.Wave));
        _noiseLayer = new NoiseLayer(NoiseLayerIndex, sampleRate, Parameters, Noise);
        _layers[NoiseLayerIndex] = _noiseLayer;

        _reverb = new ShimmerReverb(sampleRate);
        _fx = new MasterFx(sampleRate);
        _master = new SmoothedValue(Parameters.Get(GlobalParamId.MasterVolume), sampleRate, 20f);
        _patternLevel = new SmoothedValue(Parameters.Get(GlobalParamId.PatternLevel), sampleRate, 20f);
        for (int i = 0; i < LayerCount; i++)
        {
            _mute[i] = new SmoothedValue(Parameters.Get(i, ParamId.Mute) >= 0.5f ? 0f : 1f, sampleRate, 10f);
            _revSend[i] = new SmoothedValue(ParamCurves.SendGain(Parameters.Get(i, ParamId.ReverbSend)), sampleRate, 20f);
            _shimSend[i] = new SmoothedValue(ParamCurves.SendGain(Parameters.Get(i, ParamId.ShimmerSend)), sampleRate, 20f);
        }
    }

    // ------------------------------------------------------------- control API (any thread)

    public bool NoteOn(int layer, int note, int velocity) => Post(EngineEventType.NoteOn, layer, note, velocity, 0f);
    public bool NoteOff(int layer, int note) => Post(EngineEventType.NoteOff, layer, note, 0, 0f);
    public bool AllNotesOff() => Post(EngineEventType.AllNotesOff, 0, 0, 0, 0f);
    /// <summary>HOLD (OK + key): latches the key on the layer, or releases it if already latched.</summary>
    public bool HoldToggle(int layer, int note, int velocity) => Post(EngineEventType.HoldToggle, layer, note, velocity, 0f);
    /// <summary>CLR + OK: releases all HOLD-latched keys of one layer.</summary>
    public bool ClearHolds(int layer) => Post(EngineEventType.ClearHolds, layer, 0, 0, 0f);
    /// <summary>CLR + PLAY: releases all HOLD-latched keys of every layer.</summary>
    public bool ClearAllHolds() => Post(EngineEventType.ClearAllHolds, 0, 0, 0, 0f);
    public bool PitchBend(float value) => Post(EngineEventType.PitchBend, 0, 0, 0, value);
    public bool ModWheel(float value) => Post(EngineEventType.ModWheel, 0, 0, 0, value);
    public bool Sustain(bool down) => Post(EngineEventType.Sustain, 0, 0, 0, down ? 1f : 0f);

    private bool Post(EngineEventType type, int layer, int note, int velocity, float value)
    {
        var ev = new EngineEvent
        {
            Type = type,
            Layer = (byte)Math.Clamp(layer, 0, 255),
            Note = (byte)Math.Clamp(note, 0, 127),
            Velocity = (byte)Math.Clamp(velocity, 0, 127),
            Value = value,
        };
        return Events.TryEnqueue(in ev);
    }

    /// <summary>
    /// Sets a layer parameter. Setting WAVE also requests the (background) build of the wavetable (layers 1-3) or of the
    /// noise sample slot (layer 4). Non-audio threads only.
    /// </summary>
    public float SetParam(int layer, ParamId id, float value)
    {
        float v = Parameters.Set(layer, id, value);
        if (id == ParamId.Wave)
        {
            if (layer == NoiseLayerIndex) Noise.EnsureBuiltAsync((int)v);
            else Bank.EnsureBuiltAsync((int)v);
        }
        else if (id == ParamId.Structure && layer != NoiseLayerIndex && StructureInfo.IsDrone((Structure)(int)v))
        {
            Parameters.Set(layer, ParamId.Octave, -2);      // as the hardware: DRONE structures default to octave -2
        }
        return v;
    }

    public float SetGlobal(GlobalParamId id, float value) => Parameters.Set(id, value);

    /// <summary>Attaches the sequencer that <see cref="Render"/> drives (non-audio thread; one sequencer per engine).</summary>
    public void AttachSequencer(IEngineSequencer sequencer)
    {
        sequencer.Bind(this);
        Volatile.Write(ref _seq, sequencer);
    }

    // ------------------------------------------------------------- ISequencerHost (audio thread)

    ParameterStore ISequencerHost.Parameters => Parameters;

    void ISequencerHost.SeqNoteOn(int layer, int note, int velocity)
    {
        if ((uint)layer >= (uint)_layers.Length) return;
        if (Parameters.Get(GlobalParamId.MuteMode) >= 0.5f && Parameters.Get(layer, ParamId.Mute) >= 0.5f) return;   // SEQ mute
        _layers[layer].NoteOn(note, velocity);
    }

    void ISequencerHost.SeqNoteOff(int layer, int note)
    {
        if ((uint)layer < (uint)_layers.Length) _layers[layer].NoteOff(note);
    }

    void ISequencerHost.ReleaseAllNotes()
    {
        foreach (var l in _layers) l.AllNotesOff();
    }

    void ISequencerHost.ResetReverb() => _reverb.Reset();

    // ------------------------------------------------------------- UI snapshot (lock-free)

    public int ActiveVoices => Volatile.Read(ref _pubVoices);
    public float PeakLeft => Volatile.Read(ref _pubPeakL);
    public float PeakRight => Volatile.Read(ref _pubPeakR);
    public long DroppedEvents => Events.Dropped;

    // ------------------------------------------------------------- audio thread

    public void Render(Span<float> interleavedStereo, int frames)
    {
        var buf = interleavedStereo.Slice(0, frames * 2);
        buf.Clear();

        while (Events.TryDequeue(out var ev)) Dispatch(in ev);

        var seq = Volatile.Read(ref _seq);
        seq?.BeginBlock(frames);

        float pl = 0f, pr = 0f;
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(MaxBlock, frames - done);
            if (seq != null)
            {
                seq.ProcessDueEvents();                       // may start/stop notes and change parameters at this exact frame
                n = Math.Min(n, Math.Max(1, seq.SamplesToNextEvent));
            }
            ApplyGlobals();
            var chunk = buf.Slice(done * 2, n * 2);
            RenderChunk(chunk, n, ref pl, ref pr);
            if (seq != null)
            {
                seq.MixPost(chunk, n);
                seq.Advance(n);
            }
            done += n;
        }

        int voices = 0;
        foreach (var layer in _layers) voices += layer.ActiveVoices;
        _peakL = Math.Max(pl, _peakL * PeakDecayPerBlock);
        _peakR = Math.Max(pr, _peakR * PeakDecayPerBlock);
        Volatile.Write(ref _pubPeakL, _peakL);
        Volatile.Write(ref _pubPeakR, _peakR);
        Volatile.Write(ref _pubVoices, voices);
    }

    /// <summary>Test hook: when set, called with "reverb", "fx" and "master" as these stages run (in order) for each chunk.</summary>
    internal Action<string>? StageTrace;

    // Bus contents of the most recent chunk (test hooks).
    internal ReadOnlySpan<float> DryBusL => _dryL;
    internal ReadOnlySpan<float> DryBusR => _dryR;
    internal ReadOnlySpan<float> ReverbSendBusL => _revInL;
    internal ReadOnlySpan<float> ReverbSendBusR => _revInR;
    internal ReadOnlySpan<float> ShimmerSendBusL => _shimInL;
    internal ReadOnlySpan<float> ShimmerSendBusR => _shimInR;

    private void ApplyGlobals()
    {
        int rt = (int)Parameters.Get(GlobalParamId.ReverbType);
        if (rt != _lastReverbType) { _lastReverbType = rt; _reverb.SetType((ReverbType)rt); }
        float rm = Parameters.Get(GlobalParamId.ReverbMix);
        if (rm != _lastReverbMix) { _lastReverbMix = rm; _reverb.SetMix(rm); }

        int ft = (int)Parameters.Get(GlobalParamId.FxType);
        if (ft != _lastFxType) { _lastFxType = ft; _fx.SetType((MasterFxType)ft); }
        float fs = Parameters.Get(GlobalParamId.FxSpeed);
        if (fs != _lastFxSpeed) { _lastFxSpeed = fs; _fx.SetSpeed(fs); }
        float fa = Parameters.Get(GlobalParamId.FxAmount);
        if (fa != _lastFxAmount) { _lastFxAmount = fa; _fx.SetAmount(fa); }
        float bpm = Parameters.Get(GlobalParamId.Bpm);
        if (bpm != _lastBpm) { _lastBpm = bpm; _fx.SetTempo(bpm); }

        _master.SetTarget(Parameters.Get(GlobalParamId.MasterVolume));
        _patternLevel.SetTarget(Parameters.Get(GlobalParamId.PatternLevel));
        for (int i = 0; i < LayerCount; i++)
        {
            // SND mute silences the layer output; in SEQ mute mode only the sequencer notes are dropped (see SeqNoteOn)
            bool silence = Parameters.Get(i, ParamId.Mute) >= 0.5f && Parameters.Get(GlobalParamId.MuteMode) < 0.5f;
            _mute[i].SetTarget(silence ? 0f : 1f);
            // LFO -> REVERB / SHIMMER send modulation (layer-level LFO, from the previous block), in send-knob units
            var lm = (_layers[i] as IModulatedLayer)?.Mod;
            float rOff = lm?.ReverbSendOffset ?? 0f, sOff = lm?.ShimmerSendOffset ?? 0f;
            _revSend[i].SetTarget(ParamCurves.SendGain(Parameters.Get(i, ParamId.ReverbSend) + rOff));
            _shimSend[i].SetTarget(ParamCurves.SendGain(Parameters.Get(i, ParamId.ShimmerSend) + sOff));
        }
    }

    private void RenderChunk(Span<float> output, int n, ref float peakL, ref float peakR)
    {
        var dryL = _dryL.AsSpan(0, n); var dryR = _dryR.AsSpan(0, n);
        var revL = _revInL.AsSpan(0, n); var revR = _revInR.AsSpan(0, n);
        var shL = _shimInL.AsSpan(0, n); var shR = _shimInR.AsSpan(0, n);
        var wetL = _wetL.AsSpan(0, n); var wetR = _wetR.AsSpan(0, n);
        dryL.Clear(); dryR.Clear(); revL.Clear(); revR.Clear(); shL.Clear(); shR.Clear();

        var scratch = _layerBuf.AsSpan(0, n * 2);
        for (int li = 0; li < LayerCount; li++)
        {
            scratch.Clear();
            _layers[li].Render(scratch, n);
            for (int i = 0; i < n; i++)
            {
                float m = _mute[li].Next();
                float rs = _revSend[li].Next();
                float ss = _shimSend[li].Next();
                float l = scratch[2 * i] * m, r = scratch[2 * i + 1] * m;
                dryL[i] += l; dryR[i] += r;
                revL[i] += l * rs; revR[i] += r * rs;
                shL[i] += l * ss; shR[i] += r * ss;
            }
        }

        StageTrace?.Invoke("reverb");
        _reverb.Process(revL, revR, shL, shR, wetL, wetR);
        for (int i = 0; i < n; i++) { dryL[i] += wetL[i]; dryR[i] += wetR[i]; }

        // FX routing (as the original): POST = master FX after the pattern level, so FX tails survive PTN LVL 0;
        // PRE = master FX before the pattern level, so PTN LVL 0 silences everything.
        bool fxPre = Parameters.Get(GlobalParamId.FxRouting) >= 0.5f;
        if (!fxPre) ApplyPatternLevel(dryL, dryR);
        StageTrace?.Invoke("fx");
        _fx.Process(dryL, dryR);
        if (fxPre) ApplyPatternLevel(dryL, dryR);

        StageTrace?.Invoke("master");
        for (int i = 0; i < n; i++)
        {
            float g = ParamCurves.LevelGain(_master.Next());
            float l = SoftClip(dryL[i] * g);
            float r = SoftClip(dryR[i] * g);
            output[2 * i] = l; output[2 * i + 1] = r;
            float al = MathF.Abs(l), ar = MathF.Abs(r);
            if (al > peakL) peakL = al;
            if (ar > peakR) peakR = ar;
        }
    }

    private void Dispatch(in EngineEvent ev)
    {
        switch (ev.Type)
        {
            case EngineEventType.NoteOn:
                if (ev.Layer < _layers.Length) _layers[ev.Layer].NoteOn(ev.Note, ev.Velocity);
                break;
            case EngineEventType.NoteOff:
                if (ev.Layer < _layers.Length) _layers[ev.Layer].NoteOff(ev.Note);
                break;
            case EngineEventType.HoldToggle:
                if (ev.Layer < _layers.Length) _layers[ev.Layer].HoldToggle(ev.Note, ev.Velocity);
                break;
            case EngineEventType.ClearHolds:
                if (ev.Layer < _layers.Length) _layers[ev.Layer].ClearHolds();
                break;
            case EngineEventType.ClearAllHolds:
                foreach (var l in _layers) l.ClearHolds();
                break;
            case EngineEventType.AllNotesOff:
                foreach (var l in _layers) l.AllNotesOff();
                break;
            case EngineEventType.PitchBend:
                foreach (var l in _layers) l.SetPitchBend(ev.Value);
                break;
            case EngineEventType.ModWheel:
                foreach (var l in _layers) l.SetModWheel(ev.Value);
                break;
            case EngineEventType.Sustain:
                foreach (var l in _layers) l.SetSustain(ev.Value >= 0.5f);
                break;
        }
    }

    /// <summary>Linear below the knee (-3 dBFS), tanh-shaped above it; asymptote 1.0, NaN -> 0.</summary>
    private void ApplyPatternLevel(Span<float> l, Span<float> r)
    {
        for (int i = 0; i < l.Length; i++)
        {
            float g = ParamCurves.LevelGain(_patternLevel.Next());
            l[i] *= g; r[i] *= g;
        }
    }

    public static float SoftClip(float x)
    {
        float a = MathF.Abs(x);
        if (a <= ClipKnee) return x;
        if (float.IsNaN(x)) return 0f;
        float y = ClipKnee + (1f - ClipKnee) * MathF.Tanh((a - ClipKnee) / (1f - ClipKnee));
        y = Math.Min(y, 1f);
        return x < 0f ? -y : y;
    }

    /// <summary>Test access to a synth layer (0..2).</summary>
    public SynthLayer GetLayer(int index) => _synthLayers[index];

    /// <summary>Test access to the NOISE layer (layer index 3).</summary>
    public NoiseLayer NoiseLayer => _noiseLayer;

    /// <summary>Test access to any layer by index (0..3).</summary>
    public ILayer GetAnyLayer(int index) => _layers[index];
}
