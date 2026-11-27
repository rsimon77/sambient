using Sambient.Engine;

namespace Sambient.Seq;

public static class SeqLimits
{
    public const int Tracks = 4;
    public const int MaxSteps = 64;
    public const int MaxNotesPerStep = 8;
    public const int MinProbability = 25;
}

/// <summary>Which layer parameters a step can lock (the hardware forbids FX SPEED/AMOUNT, tune all, reverb mix, BPM and VOL: those are global parameters here, so never lockable).</summary>
public static class LockRules
{
    /// <summary>Layer parameters that cannot be locked: they reconfigure the layer or the sequencer itself rather than shape a sound.</summary>
    public static bool IsLockable(ParamId id) => id is not (ParamId.Mute or ParamId.SeqLength or ParamId.RndStep
        or ParamId.TuningKey or ParamId.StepNote or ParamId.Structure or ParamId.VoiceMode);

    /// <summary>"Sound lock": WAVE, HARMONIC, Balance, PITCH, Detune, MOD rate and MOD depth together.</summary>
    public static readonly ParamId[] SoundLock =
        [ParamId.Wave, ParamId.Harmonic, ParamId.Balance, ParamId.Pitch, ParamId.Detune, ParamId.ModRate, ParamId.ModDepth];
}

/// <summary>One note of a step: pitch, velocity and tie length (steps the note sounds for, &gt;= 1; cannot wrap past the end of the track).</summary>
public readonly record struct StepNoteData(int Note, int Velocity, int Tie)
{
    public static StepNoteData Make(int note, int velocity = 100, int tie = 1)
        => new(Math.Clamp(note, 0, 127), Math.Clamp(velocity, 1, 127), Math.Clamp(tie, 1, SeqLimits.MaxSteps));
}

/// <summary>A lock: a layer parameter value that holds while its step plays.</summary>
public readonly record struct ParamLock(ParamId Id, float Value);

/// <summary>Mutable step (UI-thread model): up to 8 notes, probability 25..100 %, parameter locks.</summary>
public sealed class StepData
{
    public readonly List<StepNoteData> Notes = new(SeqLimits.MaxNotesPerStep);
    public int Probability = 100;
    public readonly SortedDictionary<ParamId, float> Locks = new();

    public bool IsEmpty => Notes.Count == 0 && Locks.Count == 0 && Probability == 100;

    public StepData Clone()
    {
        var s = new StepData { Probability = Probability };
        s.Notes.AddRange(Notes);
        foreach (var kv in Locks) s.Locks[kv.Key] = kv.Value;
        return s;
    }

    public void Clear() { Notes.Clear(); Locks.Clear(); Probability = 100; }
}

/// <summary>The 64 steps of one track. Length / step note / RND STEP are layer parameters (SeqLength, StepNote, RndStep).</summary>
public sealed class TrackData
{
    public readonly StepData[] Steps = new StepData[SeqLimits.MaxSteps];

    public TrackData() { for (int i = 0; i < Steps.Length; i++) Steps[i] = new StepData(); }

    public TrackData Clone()
    {
        var t = new TrackData();
        for (int i = 0; i < Steps.Length; i++) t.Steps[i] = Steps[i].Clone();
        return t;
    }

    public void CopyFrom(TrackData o) { for (int i = 0; i < Steps.Length; i++) Steps[i] = o.Steps[i].Clone(); }
}

/// <summary>
/// A Pattern Palette: a name, all layer parameters (base values), the pattern-level globals and the four sequences.
/// This is the UI-side, mutable model; the audio thread plays an immutable <see cref="CompiledPattern"/> built from it.
/// </summary>
public sealed class PatternPalette
{
    public const int NameMin = 4, NameMax = 8;

    /// <summary>The global parameters stored in a palette (system settings such as master volume, tune and pitch-bend range are not).</summary>
    public static readonly GlobalParamId[] PatternGlobals =
    [
        GlobalParamId.Bpm, GlobalParamId.ReverbType, GlobalParamId.ReverbMix, GlobalParamId.FxType, GlobalParamId.FxSpeed,
        GlobalParamId.FxAmount, GlobalParamId.FxRouting, GlobalParamId.PatternLevel, GlobalParamId.Temperament,
        GlobalParamId.Transpose, GlobalParamId.RndSeq,
    ];

    private string _name = "INIT";
    public string Name { get => _name; set => _name = NormalizeName(value); }

    /// <summary>Base values [layer][(int)ParamId].</summary>
    public float[][] Layers { get; } = new float[SeqLimits.Tracks][];
    /// <summary>Global values indexed by (int)GlobalParamId (only <see cref="PatternGlobals"/> are meaningful).</summary>
    public float[] Globals { get; } = new float[ParamTable.GlobalParamCount];
    public TrackData[] Tracks { get; } = new TrackData[SeqLimits.Tracks];
    /// <summary>Explicit pattern length in 4/4 bars (1..512) used for chain switching, or 0 = the longest track cycle.</summary>
    public int PatternBars { get; set; }

    public PatternPalette(string name = "INIT")
    {
        Name = name;
        for (int l = 0; l < Layers.Length; l++)
        {
            Layers[l] = new float[ParamTable.LayerParamCount];
            foreach (var id in Enum.GetValues<ParamId>()) Layers[l][(int)id] = ParamTable.Get(id).Default;
            Tracks[l] = new TrackData();
        }
        foreach (var id in Enum.GetValues<GlobalParamId>()) Globals[(int)id] = ParamTable.Get(id).Default;
    }

    /// <summary>Trims, truncates to 8 characters and pads to 4 (blank is "INIT").</summary>
    public static string NormalizeName(string? n)
    {
        n = (n ?? "").Trim();
        if (n.Length == 0) n = "INIT";
        if (n.Length > NameMax) n = n[..NameMax];
        return n.PadRight(NameMin);
    }

    public float GetLayer(int layer, ParamId id) => Layers[layer][(int)id];
    public void SetLayer(int layer, ParamId id, float v) => Layers[layer][(int)id] = ParamTable.Clamp(id, v);
    public float GetGlobal(GlobalParamId id) => Globals[(int)id];
    public void SetGlobal(GlobalParamId id, float v) => Globals[(int)id] = ParamTable.Clamp(id, v);

    public PatternPalette Clone()
    {
        var p = new PatternPalette(Name) { PatternBars = PatternBars };
        for (int l = 0; l < Layers.Length; l++) { Array.Copy(Layers[l], p.Layers[l], Layers[l].Length); p.Tracks[l] = Tracks[l].Clone(); }
        Array.Copy(Globals, p.Globals, Globals.Length);
        return p;
    }

    /// <summary>Reads all layer parameters (BASE values) and the pattern globals from the store into this palette.</summary>
    public void CaptureParams(ParameterStore s)
    {
        for (int l = 0; l < Layers.Length; l++)
            foreach (var id in Enum.GetValues<ParamId>()) Layers[l][(int)id] = s.GetBase(l, id);
        foreach (var g in PatternGlobals) Globals[(int)g] = s.Get(g);
    }

    /// <summary>Layers may exchange sound + sequence when both are synth layers (1..3) or both are the NOISE layer (4).</summary>
    public static bool CanCopyLayer(int from, int to)
        => (uint)from < SeqLimits.Tracks && (uint)to < SeqLimits.Tracks
           && (from == ParameterStore.NoiseLayerIndex) == (to == ParameterStore.NoiseLayerIndex);
}
