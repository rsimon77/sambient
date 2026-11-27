namespace Sambient.Control;

public enum MidiPortDirection { In, Out }

/// <summary>Kind of a port as inferred from its name.</summary>
public enum MidiPortKind
{
    /// <summary>Plain/unlabelled MIDI port.</summary>
    Midi,
    /// <summary>Launchkey "DAW" port (knobs, pads, faders, LEDs in DAW mode).</summary>
    Daw,
}

public sealed record MidiPortInfo(string Name, MidiPortDirection Direction, MidiPortKind Kind);
