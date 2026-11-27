using System.Text.Json;
using Sambient.Control;

namespace Sambient.App.Settings;

/// <summary>Small persisted UI settings (~/Library/Application Support/Sambient/settings.json). Tolerates a missing or corrupt file.</summary>
public sealed class AppSettings
{
    public bool KeyboardVisible { get; set; }
    /// <summary>Octave number of the first visible C on the on-screen keyboard (1..4; C3 = MIDI 48).</summary>
    public int KeyboardOctave { get; set; } = 3;
    /// <summary>Launchkey MIDI channel 1..4 plays layer 1..4 (default off: notes go to the selected layer).</summary>
    public bool MidiChannelToLayer { get; set; }

    /// <summary>Last used pattern palette (0-based bank 0..7 / slot 0..15); loaded at startup.</summary>
    public int LastBank { get; set; }
    public int LastSlot { get; set; }
    /// <summary>Master tune is shown in Hz instead of cents.</summary>
    public bool ShowMasterTuneHz { get; set; }
    /// <summary>Master tune in cents (a system setting, not part of a palette).</summary>
    public float MasterTuneCents { get; set; }
    public bool AutoStep { get; set; }
    public bool FollowPlayhead { get; set; } = true;
    /// <summary>BPM mode GLBL (keep the current BPM when a palette loads); default PTN.</summary>
    public bool BpmGlobal { get; set; }
    public bool ResetReverbOnChange { get; set; }
    public int MetronomeVolume { get; set; } = 8;
    public int PreCount { get; set; } = 2;
    /// <summary>Expanded state of the collapsible sections (key = section name).</summary>
    public Dictionary<string, bool> Expanders { get; set; } = new();

    public bool IsExpanded(string key, bool def) => Expanders.TryGetValue(key, out var v) ? v : def;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => AppDataPaths.File("settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            s.KeyboardOctave = Math.Clamp(s.KeyboardOctave, 1, 4);
            s.LastBank = Math.Clamp(s.LastBank, 0, 7);
            s.LastSlot = Math.Clamp(s.LastSlot, 0, 15);
            s.MasterTuneCents = Math.Clamp(s.MasterTuneCents, -75f, 75f);
            s.MetronomeVolume = Math.Clamp(s.MetronomeVolume, 0, 15);
            s.PreCount = Math.Clamp(s.PreCount, 0, 8);
            s.Expanders ??= new();
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // best effort: a read-only disk must not crash the app
        }
    }
}
