using System.Text.RegularExpressions;

namespace Sambient.Control;

/// <summary>Result of Launchkey MK3 port detection.</summary>
public sealed record LaunchkeyPorts(
    string? MidiIn,
    string? MidiOut,
    string? DawIn,
    string? DawOut,
    int? Model)
{
    public static readonly LaunchkeyPorts None = new(null, null, null, null, null);

    /// <summary>True when all four ports (MIDI in/out, DAW in/out) were found.</summary>
    public bool IsComplete => MidiIn != null && MidiOut != null && DawIn != null && DawOut != null;

    /// <summary>True when at least one port was found.</summary>
    public bool IsAny => MidiIn != null || MidiOut != null || DawIn != null || DawOut != null;
}

/// <summary>
/// Pure, name-based detection of the Novation Launchkey MK3 ports.
/// Matches e.g. "Launchkey MK3 49 LKMK3 MIDI Out" / "... DAW In"; tolerant of case, spacing,
/// punctuation and a missing model number. Rejects Launchkey Mini and MK2/MK4 (and other MK versions).
/// </summary>
public static class LaunchkeyMk3Detector
{
    private static readonly Regex ModelRegex = new(@"(?<!\d)(25|37|49|61|88)(?!\d)", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^a-z0-9]", RegexOptions.Compiled);
    // Windows-style: "MIDIIN2 (Launchkey MK3 49)" / "MIDIOUT2 (...)" is the DAW port.
    private static readonly Regex SecondMidiRegex = new(@"midi(in|out)2", RegexOptions.Compiled);

    public static LaunchkeyPorts Detect(IEnumerable<string> inputNames, IEnumerable<string> outputNames)
    {
        var (midiIn, dawIn) = Pick(inputNames);
        var (midiOut, dawOut) = Pick(outputNames);
        int? model = midiIn.Model ?? midiOut.Model ?? dawIn.Model ?? dawOut.Model;
        return new LaunchkeyPorts(midiIn.Name, midiOut.Name, dawIn.Name, dawOut.Name, model);
    }

    public static LaunchkeyPorts Detect(IEnumerable<MidiPortInfo> ports)
    {
        var list = ports.ToList();
        return Detect(
            list.Where(p => p.Direction == MidiPortDirection.In).Select(p => p.Name),
            list.Where(p => p.Direction == MidiPortDirection.Out).Select(p => p.Name));
    }

    /// <summary>True if the name looks like a Launchkey MK3 port (any kind).</summary>
    public static bool IsLaunchkeyMk3(string name) => TryParse(name, out _, out _);

    /// <summary>DAW vs MIDI classification by name (works for any device; defaults to Midi).</summary>
    public static MidiPortKind ClassifyKind(string name)
    {
        var lower = name.ToLowerInvariant();
        var compact = NonAlnum.Replace(lower, "");
        if (compact.Contains("daw") || SecondMidiRegex.IsMatch(compact)) return MidiPortKind.Daw;
        return MidiPortKind.Midi;
    }

    /// <summary>Parses a port name; returns false if it is not a Launchkey MK3 port.</summary>
    public static bool TryParse(string? name, out MidiPortKind kind, out int? model)
    {
        kind = MidiPortKind.Midi;
        model = null;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var lower = name.ToLowerInvariant();
        var compact = NonAlnum.Replace(lower, "");

        bool hasBrand = compact.Contains("launchkey") || compact.Contains("lkmk3");
        if (!hasBrand) return false;
        if (compact.Contains("mini") || compact.Contains("lkmini")) return false;
        // Explicitly other generations.
        if (compact.Contains("mk2") || compact.Contains("mk4") || compact.Contains("mk1")) return false;
        if (!compact.Contains("mk3")) return false;

        kind = ClassifyKind(name);
        var m = ModelRegex.Match(lower);
        if (m.Success) model = int.Parse(m.Value);
        return true;
    }

    private static ((string? Name, int? Model) Midi, (string? Name, int? Model) Daw) Pick(IEnumerable<string> names)
    {
        (string? Name, int? Model) midi = (null, null), daw = (null, null);
        foreach (var n in names)
        {
            if (!TryParse(n, out var kind, out var model)) continue;
            if (kind == MidiPortKind.Daw)
            {
                if (daw.Name == null || (daw.Model == null && model != null)) daw = (n, model);
            }
            else if (midi.Name == null || (midi.Model == null && model != null))
            {
                midi = (n, model);
            }
        }
        return (midi, daw);
    }
}
