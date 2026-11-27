namespace Sambient.Control;

/// <summary>
/// Known MIDI-map target ids (parameter names). The JSON map format is unchanged: a binding's "target" is simply one of these
/// strings (older files keep working; unknown targets are ignored by the application).
/// Layer targets address the SELECTED layer (like the hardware); global targets are layer independent.
/// </summary>
public static class MidiTargets
{
    // layer parameters (selected layer)
    public const string Harmonic = "Harmonic";
    public const string Cutoff = "Cutoff";
    public const string Resonance = "Resonance";
    public const string Attack = "Attack";
    public const string Release = "Release";
    public const string Level = "Level";
    public const string Pan = "Pan";
    public const string Velocity = "Velocity";
    /// <summary>Selected layer's send to the REVERB bus (new in M2).</summary>
    public const string ReverbSend = "ReverbSend";
    /// <summary>Selected layer's send to the SHIMMER bus (new in M2).</summary>
    public const string ShimmerSend = "ShimmerSend";

    // layer parameters added with STRUCTURE / MODULATION / VOICE MODE (selected layer; names equal the engine ParamId names)
    public const string Pitch = "Pitch";
    public const string Detune = "Detune";
    public const string Balance = "Balance";
    public const string ModRate = "ModRate";
    public const string ModDepth = "ModDepth";
    public const string Lfo1Rate = "Lfo1Rate";
    public const string Lfo1Depth = "Lfo1Depth";
    public const string Lfo2Rate = "Lfo2Rate";
    public const string Lfo2Depth = "Lfo2Depth";
    public const string Glide = "Glide";
    public const string UnisonDetune = "UnisonDetune";

    // global parameters
    public const string MasterVolume = "MasterVolume";
    public const string ReverbMix = "ReverbMix";
    public const string FxSpeed = "FxSpeed";
    public const string FxAmount = "FxAmount";

    /// <summary>Pattern level (PTN LVL).</summary>
    public const string PatternLevel = "PatternLevel";
    /// <summary>Master tune in cents (system setting).</summary>
    public const string MasterTuneCents = "MasterTuneCents";
    /// <summary>TUNE ALL in cents (real-time).</summary>
    public const string TuneAllCents = "TuneAllCents";

    // transport buttons (momentary: a CC value >= 64 presses the button). The CC numbers the Launchkey MK3 transport buttons
    // send in its default (non-DAW) mode are NOT confirmed yet, so there are no default bindings: use MIDI learn.
    public const string TransportPlay = "TransportPlay";
    public const string TransportStop = "TransportStop";
    public const string TransportRecord = "TransportRecord";
    public const string TransportPrmRec = "TransportPrmRec";

    public static readonly IReadOnlyList<string> Transport = [TransportPlay, TransportStop, TransportRecord, TransportPrmRec];

    /// <summary>True for the MIDI-learnable transport button targets.</summary>
    public static bool IsTransportTarget(string target) => Transport.Contains(target);

    public static readonly IReadOnlyList<string> Layer =
        [Harmonic, Cutoff, Resonance, Attack, Release, Level, Pan, Velocity, ReverbSend, ShimmerSend,
         Pitch, Detune, Balance, ModRate, ModDepth, Lfo1Rate, Lfo1Depth, Lfo2Rate, Lfo2Depth, Glide, UnisonDetune];

    public static readonly IReadOnlyList<string> Global = [MasterVolume, ReverbMix, FxSpeed, FxAmount, PatternLevel, MasterTuneCents, TuneAllCents];

    /// <summary>True for targets that apply to the selected layer.</summary>
    public static bool IsLayerTarget(string target) => Layer.Contains(target);
}
