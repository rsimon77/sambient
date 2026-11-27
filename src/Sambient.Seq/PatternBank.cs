namespace Sambient.Seq;

/// <summary>
/// 8 banks x 16 = 128 palette slots. Banks 1-4 are factory presets (<see cref="FactoryPresets"/>), banks 5-8 start as INIT.
/// A saved user file wins over the preset of the same slot (delete it with <see cref="RevertToFactory"/>). Files live in
/// <see cref="DefaultDirectory"/> (~/Library/Application Support/Sambient/patterns), one JSON file per palette: B5-03.json
/// (bank 5, slot 3; both 1-based). Bank / slot arguments in this API are 0-based. Thread-safe.
/// </summary>
public sealed class PatternBank
{
    public const int Banks = 8, SlotsPerBank = 16, PresetBanks = 4;
    private readonly object _gate = new();
    private readonly string?[] _names = new string?[Banks * SlotsPerBank];

    public string Directory { get; }

    public PatternBank(string? directory = null) { Directory = directory ?? DefaultDirectory; }

    public static string DefaultDirectory { get; } = Path.Combine(
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sambient", "patterns");

    public static bool IsPresetBank(int bank) => bank is >= 0 and < PresetBanks;

    /// <summary>Display id of a slot, "B5-03".</summary>
    public static string SlotId(int bank, int slot) => $"B{bank + 1}-{slot + 1:00}";

    public static int SlotIndex(int bank, int slot) => bank * SlotsPerBank + slot;

    private static void Check(int bank, int slot)
    {
        if ((uint)bank >= Banks) throw new ArgumentOutOfRangeException(nameof(bank));
        if ((uint)slot >= SlotsPerBank) throw new ArgumentOutOfRangeException(nameof(slot));
    }

    private string PathOf(int bank, int slot) => Path.Combine(Directory, SlotId(bank, slot) + ".json");

    /// <summary>True when the user saved a file for the slot.</summary>
    public bool HasUserFile(int bank, int slot) { Check(bank, slot); return File.Exists(PathOf(bank, slot)); }

    /// <summary>True when the slot currently plays the factory preset (preset bank, no user copy).</summary>
    public bool IsFactory(int bank, int slot) => IsPresetBank(bank) && !HasUserFile(bank, slot);

    /// <summary>A fresh working copy of the palette in the slot: the user file if it loads, else the factory preset, else INIT.</summary>
    public PatternPalette Load(int bank, int slot)
    {
        Check(bank, slot);
        PatternPalette? p = null;
        string path = PathOf(bank, slot);
        if (File.Exists(path))
        {
            try { p = PatternJson.TryParse(File.ReadAllText(path)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        p ??= FactoryPresets.Get(bank, slot);
        p ??= new PatternPalette("INIT");
        lock (_gate) _names[SlotIndex(bank, slot)] = p.Name;
        return p;
    }

    /// <summary>Palette name of a slot (cached after the first load).</summary>
    public string GetName(int bank, int slot)
    {
        Check(bank, slot);
        lock (_gate) { var n = _names[SlotIndex(bank, slot)]; if (n != null) return n; }
        return Load(bank, slot).Name;
    }

    /// <summary>Writes the palette (atomically) as the user copy of the slot.</summary>
    public void Save(int bank, int slot, PatternPalette palette)
    {
        Check(bank, slot);
        System.IO.Directory.CreateDirectory(Directory);
        string path = PathOf(bank, slot), tmp = path + ".tmp";
        File.WriteAllText(tmp, PatternJson.Serialize(palette));
        File.Move(tmp, path, overwrite: true);
        lock (_gate) _names[SlotIndex(bank, slot)] = palette.Name;
    }

    /// <summary>Deletes the user copy (a preset slot plays its factory palette again; a user slot becomes INIT). True if a file was removed.</summary>
    public bool RevertToFactory(int bank, int slot)
    {
        Check(bank, slot);
        string path = PathOf(bank, slot);
        bool had = File.Exists(path);
        if (had) File.Delete(path);
        lock (_gate) _names[SlotIndex(bank, slot)] = null;
        return had;
    }
}
