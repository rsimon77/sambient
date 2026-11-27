using Melanchall.DryWetMidi.Multimedia;

namespace Sambient.Control;

/// <summary>Lists CoreMIDI (via DryWetMIDI) input and output devices.</summary>
public static class MidiPortCatalog
{
    public static IReadOnlyList<MidiPortInfo> ListAll()
    {
        var result = new List<MidiPortInfo>();
        result.AddRange(ListInputs());
        result.AddRange(ListOutputs());
        return result;
    }

    public static IReadOnlyList<MidiPortInfo> ListInputs()
    {
        var list = new List<MidiPortInfo>();
        foreach (var d in InputDevice.GetAll())
        {
            try { list.Add(new MidiPortInfo(d.Name, MidiPortDirection.In, KindOf(d.Name))); }
            finally { d.Dispose(); }
        }
        return list;
    }

    public static IReadOnlyList<MidiPortInfo> ListOutputs()
    {
        var list = new List<MidiPortInfo>();
        foreach (var d in OutputDevice.GetAll())
        {
            try { list.Add(new MidiPortInfo(d.Name, MidiPortDirection.Out, KindOf(d.Name))); }
            finally { d.Dispose(); }
        }
        return list;
    }

    private static MidiPortKind KindOf(string name) => LaunchkeyMk3Detector.ClassifyKind(name);
}
