using Sambient.Audio;
using Sambient.Dsp.Params;
using Sambient.Dsp.Wavetables;

namespace Sambient.Engine;

/// <summary>
/// The M1 synth engine: ParameterStore + lock-free event queue + 1 SynthLayer (layer index 0; the array is ready for
/// 3 synth layers + a noise layer), master volume and a safety soft clipper. <see cref="Render"/> never allocates, locks
/// or blocks. All public setters are safe from any thread. To change the sample rate create a new engine (non-RT) and
/// share the ParameterStore / WavetableBank with it.
/// </summary>
public sealed class AudioEngine : IAudioCallback
{
    public const int SynthLayerCount = 1;
    /// <summary>Soft clipper knee (-3 dBFS); output never exceeds 1.0.</summary>
    public const float ClipKnee = 0.7079458f;
    private const float PeakDecayPerBlock = 0.9f;

    private readonly SynthLayer[] _layers = new SynthLayer[SynthLayerCount];
    private SmoothedValue _master;
    private float _peakL, _peakR;                 // audio thread running peaks (with decay)
    private float _pubPeakL, _pubPeakR;           // published (Volatile)
    private int _pubVoices;

    public int SampleRate { get; }
    public ParameterStore Parameters { get; }
    public WavetableBank Bank { get; }
    public EngineEventQueue Events { get; }

    public AudioEngine(int sampleRate, ParameterStore? parameters = null, WavetableBank? bank = null)
    {
        SampleRate = sampleRate;
        Parameters = parameters ?? new ParameterStore();
        Bank = bank ?? new WavetableBank();
        Events = new EngineEventQueue(1024);
        for (int i = 0; i < _layers.Length; i++)
        {
            Bank.EnsureBuiltAsync((int)Parameters.Get(i, ParamId.Wave));
            _layers[i] = new SynthLayer(i, sampleRate, Parameters, Bank);
        }
        Bank.EnsureBuiltAsync(WavetableBank.FallbackIndex);
        _master = new SmoothedValue(Parameters.Get(GlobalParamId.MasterVolume), sampleRate, 20f);
    }

    // ------------------------------------------------------------- control API (any thread)

    public bool NoteOn(int layer, int note, int velocity) => Post(EngineEventType.NoteOn, layer, note, velocity, 0f);
    public bool NoteOff(int layer, int note) => Post(EngineEventType.NoteOff, layer, note, 0, 0f);
    public bool AllNotesOff() => Post(EngineEventType.AllNotesOff, 0, 0, 0, 0f);
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

    /// <summary>Sets a layer parameter. Setting WAVE also requests the (background) wavetable build. Non-audio threads only.</summary>
    public float SetParam(int layer, ParamId id, float value)
    {
        float v = Parameters.Set(layer, id, value);
        if (id == ParamId.Wave) Bank.EnsureBuiltAsync((int)v);
        return v;
    }

    public float SetGlobal(GlobalParamId id, float value) => Parameters.Set(id, value);

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

        int voices = 0;
        foreach (var layer in _layers)
        {
            layer.Render(buf, frames);
            voices += layer.ActiveVoices;
        }

        _master.SetTarget(Parameters.Get(GlobalParamId.MasterVolume));
        float pl = 0f, pr = 0f;
        for (int i = 0; i < frames; i++)
        {
            float g = ParamCurves.LevelGain(_master.Next());
            float l = SoftClip(buf[2 * i] * g);
            float r = SoftClip(buf[2 * i + 1] * g);
            buf[2 * i] = l; buf[2 * i + 1] = r;
            float al = MathF.Abs(l), ar = MathF.Abs(r);
            if (al > pl) pl = al;
            if (ar > pr) pr = ar;
        }
        _peakL = Math.Max(pl, _peakL * PeakDecayPerBlock);
        _peakR = Math.Max(pr, _peakR * PeakDecayPerBlock);
        Volatile.Write(ref _pubPeakL, _peakL);
        Volatile.Write(ref _pubPeakR, _peakR);
        Volatile.Write(ref _pubVoices, voices);
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
    public static float SoftClip(float x)
    {
        float a = MathF.Abs(x);
        if (a <= ClipKnee) return x;
        if (float.IsNaN(x)) return 0f;
        float y = ClipKnee + (1f - ClipKnee) * MathF.Tanh((a - ClipKnee) / (1f - ClipKnee));
        y = Math.Min(y, 1f);
        return x < 0f ? -y : y;
    }

    /// <summary>Test access to a synth layer.</summary>
    public SynthLayer GetLayer(int index) => _layers[index];
}
