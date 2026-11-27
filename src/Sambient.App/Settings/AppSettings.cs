using System.Text.Json;
using Sambient.Control;

namespace Sambient.App.Settings;

/// <summary>Small persisted UI settings (~/Library/Application Support/Sambient/settings.json). Tolerates a missing or corrupt file.</summary>
public sealed class AppSettings
{
    public bool KeyboardVisible { get; set; }
    /// <summary>Octave number of the first visible C on the on-screen keyboard (1..4; C3 = MIDI 48).</summary>
    public int KeyboardOctave { get; set; } = 3;

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
