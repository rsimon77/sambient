namespace Sambient.Engine;

/// <summary>Per-layer parameters (M1: synth layers). Values are stored in hardware units unless noted.</summary>
public enum ParamId
{
    /// <summary>WAVE slot 1..32 (integer).</summary>
    Wave,
    /// <summary>HARMONIC 0..127 (wavetable frame).</summary>
    Harmonic,
    /// <summary>Filter type, an <see cref="FilterType"/> value as float (0..3).</summary>
    FilterType,
    /// <summary>CUTOFF 0..127 (70 Hz .. 14.4 kHz, exponential).</summary>
    Cutoff,
    /// <summary>RESO 0..127 (Q, or bandwidth in BP mode).</summary>
    Resonance,
    /// <summary>ATTACK 0..127 (0..10 s).</summary>
    Attack,
    /// <summary>RELEASE 0..127 (0..10 s).</summary>
    Release,
    /// <summary>LEVEL 0..127 (63.5 = 0 dB).</summary>
    Level,
    /// <summary>PAN -63..+63.</summary>
    Pan,
    /// <summary>Fixed velocity 1..127 used by note sources that have no velocity (computer keyboard).</summary>
    Velocity,
    /// <summary>Octave shift -3..+3 (integer).</summary>
    Octave,
    /// <summary>Send to the REVERB bus 0..127 (post level, post pan).</summary>
    ReverbSend,
    /// <summary>Send to the SHIMMER bus 0..127 (post level, post pan).</summary>
    ShimmerSend,
    /// <summary>Layer mute, 0 or 1 (SND mode: silences the layer output including its sends).</summary>
    Mute,
    /// <summary>NOISE layer only: 0 = LOOP, 1 = SHOT (a <c>SamplePlayMode</c> value).</summary>
    NoisePlayMode,

    // ---- BlendWave structure & oscillator (M3) ----
    /// <summary>STRUCTURE, a <see cref="Structure"/> value (synth layers). DRONE structures default to octave -2 and allow 20 s release.</summary>
    Structure,
    /// <summary>PITCH -240..+240: pitch of the second/third OSC (units: 1/20 semitone = 5 cents, ±12 semitones). ATMOS2: modulator ratio 0.50..15.99 (mapped from the same range).</summary>
    Pitch,
    /// <summary>detune knob 0..127, center 63.5 = 0 (-63..+63 cents-ish fluctuation). ATMOS2: FM feedback 0..127.</summary>
    Detune,
    /// <summary>balance knob 0..127, center 63.5 (OSC balance). ATMOS2: FM amount 0..127. NOISE layer: unused (HARMONIC is its balance).</summary>
    Balance,
    /// <summary>MOD rate 0..127 (structure-specific MOD LFO, retriggered on note-on).</summary>
    ModRate,
    /// <summary>MOD depth 0..127.</summary>
    ModDepth,
    /// <summary>MOD shape, an <see cref="LfoShape"/> value.</summary>
    ModShape,

    // ---- LFO1 / LFO2 (M3) ----
    Lfo1Shape,   // LfoShape
    Lfo1Assign,  // LfoAssign
    Lfo1Trig,    // 0 = OFF (free-running), 1..8 = cycles after note-on, 9 = INF (retrigger every note-on)
    Lfo1Rate,    // 0..127
    Lfo1Depth,   // 0..127
    Lfo2Shape,
    Lfo2Assign,
    Lfo2Trig,
    Lfo2Rate,
    Lfo2Depth,

    // ---- Voice mode (M3) ----
    /// <summary>A <see cref="VoiceMode"/> value.</summary>
    VoiceMode,
    /// <summary>Glide 0..127 (0..10 s) for MONO / LEGATO.</summary>
    Glide,
    /// <summary>Unison detune 0..127 (0..20 cents) for UNISON.</summary>
    UnisonDetune,
    /// <summary>An <see cref="ArpType"/> value for ARP.</summary>
    ArpType,
    /// <summary>Step note length index (sequencer NOTE, also the arpeggiator speed), a <see cref="StepNote"/> value.</summary>
    StepNote,

    // ---- Sequencer track (M4) ----
    /// <summary>Sequence length 1..64 steps.</summary>
    SeqLength,
    /// <summary>RND STEP unit index: 0 = OFF, 1 = 1, 2 = 2, 3 = 4, 4 = 8, 5 = 16 steps.</summary>
    RndStep,

    // ---- Tuning (M4) ----
    /// <summary>Tonic / concert-pitch index for the pattern temperament (layer-specific so SLFG/CHKR/PLNT can differ per layer):
    /// PR.MJ..PY.FL: 0..11 = C..B; SLFG: 0..8 = 174, 285, 396, 417, 528, 639, 741, 852, 963 Hz; CHKR/PLNT: 0..6 = 1st..7th.</summary>
    TuningKey,
}

/// <summary>Global parameters.</summary>
public enum GlobalParamId
{
    /// <summary>MASTER VOL 0..127 (63.5 = 0 dB).</summary>
    MasterVolume,
    /// <summary>Pitch bend range in semitones (0..24).</summary>
    PitchBendRange,
    /// <summary>Reverb algorithm, a <c>ReverbType</c> value (0 = Off .. 9).</summary>
    ReverbType,
    /// <summary>Reverb wet return level 0..127.</summary>
    ReverbMix,
    /// <summary>Master FX type, a <c>MasterFxType</c> value (0 = Off .. 6).</summary>
    FxType,
    /// <summary>FX SPEED 0..127.</summary>
    FxSpeed,
    /// <summary>FX AMOUNT 0..127.</summary>
    FxAmount,
    /// <summary>Tempo 40..250 BPM.</summary>
    Bpm,
    /// <summary>The layer (1..4) that the keyboards and layer-parameter controls address.</summary>
    SelectedLayer,

    // ---- Pattern / sequencer (M4) ----
    /// <summary>Pattern Palette level 0..127 (-inf..+6 dB, 63.5 = 0 dB).</summary>
    PatternLevel,
    /// <summary>FX routing: 0 = POST (master FX after the pattern level: tails survive level 0), 1 = PRE.</summary>
    FxRouting,
    /// <summary>RND SEQ on/off (randomized step playback).</summary>
    RndSeq,
    /// <summary>Transpose -12..+12 semitones (for SLFG/CHKR/PLNT: key shift index, same range).</summary>
    Transpose,

    // ---- Tuning (M4) ----
    /// <summary>A <see cref="Temperament"/> value (pattern setting).</summary>
    Temperament,
    /// <summary>Master tune -75..+75 cents (system setting; ignored by SLFG/CHKR/PLNT).</summary>
    MasterTuneCents,
    /// <summary>TUNE ALL -1200..+1200 cents (real-time, all layers).</summary>
    TuneAllCents,

    // ---- Sequencer (M6) ----
    /// <summary>Mute mode: 0 = SND (a muted layer is silent, whatever plays it), 1 = SEQ (only the sequencer notes of a muted layer are dropped; the keyboard still plays it).</summary>
    MuteMode,
}

/// <summary>Pattern temperaments: EQUA, PR.MJ, PR.MN, PY.SH, PY.FL, SLFG, CHKR, PLNT.</summary>
public enum Temperament { Equal, PureMajor, PureMinor, PythagoreanSharp, PythagoreanFlat, Solfeggio, Chakra, Planetary }

public enum FilterType { Off = 0, LowPass = 1, HighPass = 2, BandPass = 3 }

public readonly record struct ParamInfo(float Min, float Max, float Default, bool IsInteger);

/// <summary>Static metadata (range, default) of all parameters.</summary>
public static class ParamTable
{
    public static readonly int LayerParamCount = Enum.GetValues<ParamId>().Length;
    public static readonly int GlobalParamCount = Enum.GetValues<GlobalParamId>().Length;

    private static readonly ParamInfo[] Layer =
    [
        new(1, 32, 1, true),        // Wave
        new(0, 127, 32, false),     // Harmonic
        new(0, 3, 1, true),         // FilterType
        new(0, 127, 80, false),     // Cutoff
        new(0, 127, 30, false),     // Resonance
        new(0, 127, 50, false),     // Attack
        new(0, 127, 70, false),     // Release
        new(0, 127, 63.5f, false),   // Level
        new(-63, 63, 0, false),     // Pan
        new(1, 127, 100, false),    // Velocity
        new(-3, 3, 0, true),        // Octave
        new(0, 127, 0, false),      // ReverbSend
        new(0, 127, 0, false),      // ShimmerSend
        new(0, 1, 0, true),         // Mute
        new(0, 1, 0, true),         // NoisePlayMode
        new(0, 5, 2, true),         // Structure (PAD1)
        new(-240, 240, 0, true),    // Pitch
        new(0, 127, 63.5f, false),  // Detune (center = 0)
        new(0, 127, 63.5f, false),  // Balance (center)
        new(0, 127, 40, false),     // ModRate
        new(0, 127, 0, false),      // ModDepth
        new(0, 23, 0, true),        // ModShape (SINE)
        new(0, 23, 0, true),        // Lfo1Shape
        new(0, 16, 0, true),        // Lfo1Assign (OFF)
        new(0, 9, 0, true),         // Lfo1Trig (OFF)
        new(0, 127, 40, false),     // Lfo1Rate
        new(0, 127, 0, false),      // Lfo1Depth
        new(0, 23, 0, true),        // Lfo2Shape
        new(0, 16, 0, true),        // Lfo2Assign (OFF)
        new(0, 9, 0, true),         // Lfo2Trig (OFF)
        new(0, 127, 40, false),     // Lfo2Rate
        new(0, 127, 0, false),      // Lfo2Depth
        new(0, 4, 0, true),         // VoiceMode (POLY)
        new(0, 127, 20, false),     // Glide
        new(0, 127, 40, false),     // UnisonDetune
        new(0, 11, 0, true),        // ArpType (UP)
        new(0, 14, 13, true),       // StepNote (1/16)
        new(1, 64, 16, true),       // SeqLength
        new(0, 5, 0, true),         // RndStep (OFF)
        new(0, 11, 0, true),        // TuningKey
    ];

    private static readonly ParamInfo[] Global =
    [
        new(0, 127, 63.5f, false),  // MasterVolume
        new(0, 24, 2, true),        // PitchBendRange
        new(0, 9, 5, true),         // ReverbType (LargeM)
        new(0, 127, 64, false),     // ReverbMix
        new(0, 6, 0, true),         // FxType (Off)
        new(0, 127, 64, false),     // FxSpeed
        new(0, 127, 0, false),      // FxAmount
        new(40, 250, 90, true),     // Bpm
        new(1, 4, 1, true),         // SelectedLayer
        new(0, 127, 63.5f, false),  // PatternLevel (0 dB)
        new(0, 1, 0, true),         // FxRouting (POST)
        new(0, 1, 0, true),         // RndSeq
        new(-12, 12, 0, true),      // Transpose
        new(0, 7, 0, true),         // Temperament (EQUA)
        new(-75, 75, 0, false),     // MasterTuneCents
        new(-1200, 1200, 0, false), // TuneAllCents
        new(0, 1, 0, true),         // MuteMode (SND)
    ];

    public static ParamInfo Get(ParamId id) => Layer[(int)id];
    public static ParamInfo Get(GlobalParamId id) => Global[(int)id];

    /// <summary>Highest WAVE value of a layer: 32 wavetables on synth layers, 8 sample slots on the NOISE layer.</summary>
    public static int MaxWave(int layer) => layer == ParameterStore.NoiseLayerIndex ? 8 : 32;

    public static float Clamp(ParamId id, float v)
    {
        var i = Layer[(int)id];
        if (float.IsNaN(v)) return i.Default;
        v = Math.Clamp(v, i.Min, i.Max);
        return i.IsInteger ? MathF.Round(v) : v;
    }

    public static float Clamp(GlobalParamId id, float v)
    {
        var i = Global[(int)id];
        if (float.IsNaN(v)) return i.Default;
        v = Math.Clamp(v, i.Min, i.Max);
        return i.IsInteger ? MathF.Round(v) : v;
    }
}

/// <summary>
/// Plain-float parameter storage, indexed [layer, id]. Writers (UI / MIDI threads) use Volatile.Write, the audio
/// thread reads each value once per block with Volatile.Read. No locks, no allocation after construction.
/// Layout is ready for more layers (<see cref="MaxLayers"/>) and more parameters (extend the enums + tables).
///
/// P-lock overlay: every layer parameter has a BASE value (what the UI shows and edits, what a pattern palette saves) and an
/// optional OVERRIDE (a parameter lock of the playing sequencer step, or a short-lived engine override such as the fast
/// release of a Stop). <see cref="Get(int, ParamId)"/> returns the EFFECTIVE value (override if set, else base) and is what the
/// layers and the engine read; <see cref="GetBase"/> returns the base value for UIs and persistence. <see cref="Set(int, ParamId, float)"/>
/// always writes the base. Overrides are written only by the audio thread (sequencer) through <see cref="SetOverride"/> /
/// <see cref="ClearOverride"/> / <see cref="ClearOverrides"/>; they are plain Volatile float + flag pairs, so the overlay costs
/// one extra read per parameter and never allocates or locks. Globals have no overlay.
/// </summary>
public sealed class ParameterStore
{
    public const int MaxLayers = 4;
    /// <summary>Index (0-based) of the NOISE layer (layer 4).</summary>
    public const int NoiseLayerIndex = 3;

    private readonly float[] _layer = new float[MaxLayers * ParamTable.LayerParamCount];
    private readonly float[] _ovr = new float[MaxLayers * ParamTable.LayerParamCount];
    private readonly int[] _ovrOn = new int[MaxLayers * ParamTable.LayerParamCount];
    private readonly float[] _global = new float[ParamTable.GlobalParamCount];

    public ParameterStore()
    {
        for (int l = 0; l < MaxLayers; l++)
            foreach (var id in Enum.GetValues<ParamId>())
                _layer[Index(l, id)] = ParamTable.Get(id).Default;
        foreach (var id in Enum.GetValues<GlobalParamId>())
            _global[(int)id] = ParamTable.Get(id).Default;
    }

    private static int Index(int layer, ParamId id) => layer * ParamTable.LayerParamCount + (int)id;

    /// <summary>EFFECTIVE value: the override (P-lock) if one is active, else the base value. Read by the layers / the engine.</summary>
    public float Get(int layer, ParamId id)
    {
        int i = Index(layer, id);
        return Volatile.Read(ref _ovrOn[i]) != 0 ? Volatile.Read(ref _ovr[i]) : Volatile.Read(ref _layer[i]);
    }

    /// <summary>BASE value (what the pattern stores; what knobs and screens show). Ignores overrides.</summary>
    public float GetBase(int layer, ParamId id) => Volatile.Read(ref _layer[Index(layer, id)]);

    /// <summary>Clamps to the parameter range and stores the BASE value. Returns the stored value.</summary>
    public float Set(int layer, ParamId id, float value)
    {
        value = Normalize(layer, id, value);
        Volatile.Write(ref _layer[Index(layer, id)], value);
        return value;
    }

    private static float Normalize(int layer, ParamId id, float value)
    {
        value = ParamTable.Clamp(id, value);
        if (id == ParamId.Wave) value = Math.Min(value, ParamTable.MaxWave(layer));
        return value;
    }

    /// <summary>True while an override (P-lock) hides the base value. Audio thread (writer) or diagnostics.</summary>
    public bool HasOverride(int layer, ParamId id) => Volatile.Read(ref _ovrOn[Index(layer, id)]) != 0;

    /// <summary>Activates an override (clamped like <see cref="Set(int, ParamId, float)"/>). Audio thread only.</summary>
    public void SetOverride(int layer, ParamId id, float value)
    {
        int i = Index(layer, id);
        Volatile.Write(ref _ovr[i], Normalize(layer, id, value));
        Volatile.Write(ref _ovrOn[i], 1);
    }

    /// <summary>Removes one override: the parameter returns to its base value. Audio thread only.</summary>
    public void ClearOverride(int layer, ParamId id) => Volatile.Write(ref _ovrOn[Index(layer, id)], 0);

    /// <summary>Removes every override of a layer. Audio thread only.</summary>
    public void ClearOverrides(int layer)
    {
        int b = layer * ParamTable.LayerParamCount;
        for (int k = 0; k < ParamTable.LayerParamCount; k++) Volatile.Write(ref _ovrOn[b + k], 0);
    }

    public float Get(GlobalParamId id) => Volatile.Read(ref _global[(int)id]);

    public float Set(GlobalParamId id, float value)
    {
        value = ParamTable.Clamp(id, value);
        Volatile.Write(ref _global[(int)id], value);
        return value;
    }
}
