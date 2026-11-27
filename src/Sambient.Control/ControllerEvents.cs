namespace Sambient.Control;

/// <summary>Which Launchkey port an event came from.</summary>
public enum ControllerSource { Keys, Daw }

public readonly record struct ControllerNote(ControllerSource Source, int Channel, int Note, int Velocity);
public readonly record struct ControllerCc(ControllerSource Source, int Channel, int Controller, int Value);
/// <summary>Value is 14-bit, 0..16383, center 8192.</summary>
public readonly record struct ControllerPitchBend(ControllerSource Source, int Channel, int Value);
public readonly record struct ControllerPressure(ControllerSource Source, int Channel, int Pressure);
