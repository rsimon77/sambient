namespace Sambient.Seq;

/// <summary>
/// The 8 banks x 16 = 128 palette slots of the CURRENT <see cref="Project"/>. The slots live in memory inside the project: saving a
/// palette into a slot commits it to the project model and marks the project dirty (nothing is written to disk until the project is
/// saved). Banks 1-4 start as copies of the factory presets (<see cref="FactoryPresets"/>), banks 5-8 as INIT; "revert to factory"
/// puts the default back. Bank / slot arguments in this API are 0-based. Thread-safe.
/// </summary>
public sealed class PatternBank
{
    public const int Banks = 8, SlotsPerBank = 16, PresetBanks = 4;
    private Project _project;

    public PatternBank(Project? project = null) { _project = project ?? Project.CreateNew(); }

    /// <summary>The project whose slots this bank exposes.</summary>
    public Project Project => Volatile.Read(ref _project);

    /// <summary>Raised after <see cref="SetProject"/> replaced the project.</summary>
    public event Action? ProjectReplaced;

    /// <summary>Makes another project the current one (the caller reloads the sequencer afterwards).</summary>
    public void SetProject(Project project)
    {
        Volatile.Write(ref _project, project ?? throw new ArgumentNullException(nameof(project)));
        ProjectReplaced?.Invoke();
    }

    public static bool IsPresetBank(int bank) => bank is >= 0 and < PresetBanks;

    /// <summary>Display id of a slot, "B5-03".</summary>
    public static string SlotId(int bank, int slot) => $"B{bank + 1}-{slot + 1:00}";

    public static int SlotIndex(int bank, int slot) => bank * SlotsPerBank + slot;

    /// <summary>True when the slot differs from its factory default (the factory preset, or INIT in banks 5-8).</summary>
    public bool IsModified(int bank, int slot) => Project.IsModified(bank, slot);

    /// <summary>True when the slot currently holds the factory preset (preset bank, unmodified).</summary>
    public bool IsFactory(int bank, int slot) => IsPresetBank(bank) && !IsModified(bank, slot);

    /// <summary>A fresh working copy of the palette in the slot.</summary>
    public PatternPalette Load(int bank, int slot) => Project.GetPalette(bank, slot);

    /// <summary>Palette name of a slot.</summary>
    public string GetName(int bank, int slot) => Project.GetName(bank, slot);

    /// <summary>Commits the palette into the slot of the project (marks the project dirty when the content changed). No file is written.</summary>
    public void Save(int bank, int slot, PatternPalette palette) => Project.SetPalette(bank, slot, palette);

    /// <summary>Restores the default of the slot (factory preset; INIT in banks 5-8). True if the slot had been modified.</summary>
    public bool RevertToFactory(int bank, int slot) => Project.RevertSlot(bank, slot);
}
