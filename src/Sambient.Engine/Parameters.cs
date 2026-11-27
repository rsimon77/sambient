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
}

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
/// </summary>
public sealed class ParameterStore
{
    public const int MaxLayers = 4;
    /// <summary>Index (0-based) of the NOISE layer (layer 4).</summary>
    public const int NoiseLayerIndex = 3;

    private readonly float[] _layer = new float[MaxLayers * ParamTable.LayerParamCount];
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

    public float Get(int layer, ParamId id) => Volatile.Read(ref _layer[Index(layer, id)]);

    /// <summary>Clamps to the parameter range and stores. Returns the stored value.</summary>
    public float Set(int layer, ParamId id, float value)
    {
        value = ParamTable.Clamp(id, value);
        if (id == ParamId.Wave) value = Math.Min(value, ParamTable.MaxWave(layer));
        Volatile.Write(ref _layer[Index(layer, id)], value);
        return value;
    }

    public float Get(GlobalParamId id) => Volatile.Read(ref _global[(int)id]);

    public float Set(GlobalParamId id, float value)
    {
        value = ParamTable.Clamp(id, value);
        Volatile.Write(ref _global[(int)id], value);
        return value;
    }
}
