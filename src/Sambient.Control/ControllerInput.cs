using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace Sambient.Control;

/// <summary>
/// Opens the detected Launchkey MIDI-In and DAW-In ports and raises typed events.
/// Events are raised on a MIDI driver (non-UI, non-audio) thread. Handlers must be fast and must
/// marshal to the UI thread themselves. Channels are 0-based (0..15). Receive-only: sends nothing.
/// </summary>
public sealed class ControllerInput : IDisposable
{
    private readonly List<InputDevice> _devices = new();
    private bool _disposed;

    public LaunchkeyPorts Ports { get; }

    public event Action<ControllerNote>? NoteOn;
    public event Action<ControllerNote>? NoteOff;
    public event Action<ControllerCc>? ControlChange;
    public event Action<ControllerPitchBend>? PitchBend;
    public event Action<ControllerPressure>? ChannelPressure;

    /// <summary>Opens whichever input ports are present in <paramref name="ports"/> and starts listening.</summary>
    public ControllerInput(LaunchkeyPorts ports)
    {
        Ports = ports;
        try
        {
            Open(ports.MidiIn, ControllerSource.Keys);
            Open(ports.DawIn, ControllerSource.Daw);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Detects ports from the current CoreMIDI device list and opens them.</summary>
    public static ControllerInput OpenDetected()
    {
        var ports = LaunchkeyMk3Detector.Detect(MidiPortCatalog.ListAll());
        return new ControllerInput(ports);
    }

    /// <summary>
    /// TODO (M6.5): enter Launchkey DAW mode. The exact messages must come from Novation's official
    /// "Launchkey MK3 Programmer's Reference Guide"; deliberately not implemented yet.
    /// </summary>
    public void EnterDawMode() => throw new NotImplementedException(
        "DAW mode / LED SysEx is planned for M6.5 and must follow Novation's Programmer's Reference.");

    private void Open(string? name, ControllerSource source)
    {
        if (name == null) return;
        InputDevice? device = null;
        // GetAll() creates a device object per port; keep the first match and dispose the rest.
        foreach (var d in InputDevice.GetAll())
        {
            if (device == null && d.Name == name) device = d; else d.Dispose();
        }
        if (device == null) return;
        device.EventReceived += (_, e) => Handle(source, e.Event);
        device.StartEventsListening();
        _devices.Add(device);
    }

    private void Handle(ControllerSource source, MidiEvent ev)
    {
        switch (ev)
        {
            case NoteOnEvent on:
                var n = new ControllerNote(source, on.Channel, on.NoteNumber, on.Velocity);
                if (on.Velocity == 0) NoteOff?.Invoke(n); else NoteOn?.Invoke(n);
                break;
            case NoteOffEvent off:
                NoteOff?.Invoke(new ControllerNote(source, off.Channel, off.NoteNumber, off.Velocity));
                break;
            case Melanchall.DryWetMidi.Core.ControlChangeEvent cc:
                ControlChange?.Invoke(new ControllerCc(source, cc.Channel, cc.ControlNumber, cc.ControlValue));
                break;
            case Melanchall.DryWetMidi.Core.PitchBendEvent pb:
                PitchBend?.Invoke(new ControllerPitchBend(source, pb.Channel, pb.PitchValue));
                break;
            case ChannelAftertouchEvent at:
                ChannelPressure?.Invoke(new ControllerPressure(source, at.Channel, at.AftertouchValue));
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var d in _devices)
        {
            try { d.StopEventsListening(); } catch { }
            d.Dispose();
        }
        _devices.Clear();
    }
}
