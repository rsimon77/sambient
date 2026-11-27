namespace Sambient.Seq;

/// <summary>
/// A Sambient project: the FULL SET of what a user composes - all 128 Pattern Palettes (8 banks x 16), the song chains and the
/// project-level settings. It lives in memory (the <see cref="PatternBank"/> reads and writes its slots) and is stored as one
/// <c>.sambient</c> file by <see cref="ProjectFile"/>. Banks 1-4 start as copies of the factory presets, banks 5-8 as INIT.
///
/// DIRTY. <see cref="IsDirty"/> becomes true on every change that a file save would persist (palette written into a slot,
/// revert / init of a slot, chain or settings edits, rename) and is cleared only by a successful save (or by loading).
/// <see cref="Changed"/> fires on every dirty / name / structure change. Thread-safe.
/// </summary>
public sealed class Project
{
    public const int SlotCount = PatternBank.Banks * PatternBank.SlotsPerBank;
    public const int MaxNameLength = 64;

    private readonly object _gate = new();
    private readonly PatternPalette?[] _slots = new PatternPalette?[SlotCount];   // null = pristine default (factory preset / INIT)
    private readonly ulong[] _fp = new ulong[SlotCount];                          // fingerprint of the non-null slots
    private readonly List<SongChain> _chains = new() { new SongChain() };
    private string _name = "Untitled";
    private ProjectSettings _settings = new();
    private volatile bool _dirty;

    // factory defaults (fingerprint + name per slot): computed once per process
    private static readonly Lazy<(ulong[] Fp, string[] Names)> Defaults = new(() =>
    {
        var fp = new ulong[SlotCount]; var names = new string[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            var p = DefaultPalette(i / PatternBank.SlotsPerBank, i % PatternBank.SlotsPerBank);
            fp[i] = PatternFingerprint.Of(p); names[i] = p.Name;
        }
        return (fp, names);
    });

    public event Action? Changed;

    public string? FilePath { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Format version of the file this project was loaded from (informational).</summary>
    public int LoadedFormatVersion { get; internal set; } = ProjectFile.Version;
    /// <summary>
    /// Reserved for the future noise-sample library (user WAV samples). Raw JSON of the file's "samples" section, kept verbatim
    /// across load/save so a newer file does not lose it; null/empty when the project has no samples. See <see cref="ProjectFile"/>.
    /// </summary>
    public string? SamplesJson { get; set; }

    public bool IsDirty => _dirty;

    private Project() { }

    /// <summary>A new project: factory presets in banks 1-4, INIT in banks 5-8, one empty "Song" chain, default settings. Not dirty.</summary>
    public static Project CreateNew(string name = "Untitled", ProjectSettings? settings = null)
    {
        var p = new Project { _name = NormalizeName(name), _settings = (settings ?? new ProjectSettings()).Normalized() };
        return p;
    }

    public static string NormalizeName(string? name)
    {
        name = (name ?? "").Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Trim();
        if (name.Length == 0) name = "Untitled";
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }

    /// <summary>The factory default of a slot: the factory preset (banks 1-4) or INIT.</summary>
    public static PatternPalette DefaultPalette(int bank, int slot) => FactoryPresets.Get(bank, slot) ?? new PatternPalette("INIT");

    // ------------------------------------------------------------ dirty

    private void Raise() => Changed?.Invoke();

    public void MarkDirty() { _dirty = true; Raise(); }

    /// <summary>Clears the dirty flag (after a successful save or when a file was just loaded).</summary>
    public void MarkClean() { _dirty = false; Raise(); }

    // ------------------------------------------------------------ name + settings

    public string Name => _name;

    /// <summary>Renames the project; true (and dirty) when the name changed.</summary>
    public bool Rename(string name)
    {
        name = NormalizeName(name);
        lock (_gate) { if (name == _name) return false; _name = name; }
        MarkDirty();
        return true;
    }

    public ProjectSettings Settings { get { lock (_gate) return _settings; } }

    /// <summary>Replaces the settings; true (and dirty) when they changed.</summary>
    public bool SetSettings(ProjectSettings s)
    {
        s = s.Normalized();
        lock (_gate) { if (s == _settings) return false; _settings = s; }
        MarkDirty();
        return true;
    }

    // ------------------------------------------------------------ palettes

    private static int Index(int bank, int slot)
    {
        if ((uint)bank >= PatternBank.Banks) throw new ArgumentOutOfRangeException(nameof(bank));
        if ((uint)slot >= PatternBank.SlotsPerBank) throw new ArgumentOutOfRangeException(nameof(slot));
        return PatternBank.SlotIndex(bank, slot);
    }

    /// <summary>A fresh working copy of the palette in a slot.</summary>
    public PatternPalette GetPalette(int bank, int slot)
    {
        int i = Index(bank, slot);
        lock (_gate) { var s = _slots[i]; if (s != null) return s.Clone(); }
        return DefaultPalette(bank, slot);
    }

    public string GetName(int bank, int slot)
    {
        int i = Index(bank, slot);
        lock (_gate) { var s = _slots[i]; if (s != null) return s.Name; }
        return Defaults.Value.Names[i];
    }

    /// <summary>Fingerprint of the palette stored in a slot.</summary>
    public ulong GetFingerprint(int bank, int slot)
    {
        int i = Index(bank, slot);
        lock (_gate) return _slots[i] != null ? _fp[i] : Defaults.Value.Fp[i];
    }

    /// <summary>True when the slot differs from its factory default (preset or INIT).</summary>
    public bool IsModified(int bank, int slot) => GetFingerprint(bank, slot) != Defaults.Value.Fp[Index(bank, slot)];

    /// <summary>Stores a copy of the palette in the slot. True (and dirty) when the slot content changed.</summary>
    public bool SetPalette(int bank, int slot, PatternPalette palette)
    {
        int i = Index(bank, slot);
        ulong fp = PatternFingerprint.Of(palette);
        lock (_gate)
        {
            ulong cur = _slots[i] != null ? _fp[i] : Defaults.Value.Fp[i];
            if (fp == cur) return false;
            Store(i, palette.Clone(), fp);
        }
        MarkDirty();
        return true;
    }

    /// <summary>Puts the factory default (preset or INIT) back into the slot. True (and dirty) when it was changed.</summary>
    public bool RevertSlot(int bank, int slot)
    {
        int i = Index(bank, slot);
        lock (_gate)
        {
            bool changed = _slots[i] != null && _fp[i] != Defaults.Value.Fp[i];
            _slots[i] = null;
            if (!changed) return false;
        }
        MarkDirty();
        return true;
    }

    /// <summary>Overwrites the slot with an empty INIT palette.</summary>
    public bool InitSlot(int bank, int slot) => SetPalette(bank, slot, new PatternPalette("INIT"));

    /// <summary>Loader path: stores a palette without touching the dirty flag.</summary>
    internal void LoadSlot(int bank, int slot, PatternPalette palette)
    {
        int i = Index(bank, slot);
        ulong fp = PatternFingerprint.Of(palette);
        lock (_gate) Store(i, fp == Defaults.Value.Fp[i] ? null : palette, fp);
    }

    private void Store(int i, PatternPalette? p, ulong fp) { _slots[i] = p; _fp[i] = fp; }

    // ------------------------------------------------------------ chains

    public int ChainCount { get { lock (_gate) return _chains.Count; } }

    /// <summary>A copy of chain <paramref name="index"/> (0 = the "Song" chain).</summary>
    public SongChain GetChain(int index = 0)
    {
        lock (_gate) return _chains[Math.Clamp(index, 0, _chains.Count - 1)].Clone();
    }

    public IReadOnlyList<SongChain> GetChains() { lock (_gate) return _chains.Select(c => c.Clone()).ToList(); }

    /// <summary>The main song chain (index 0).</summary>
    public SongChain Song => GetChain(0);

    /// <summary>Replaces a chain (normalized); true (and dirty) when it changed.</summary>
    public bool SetChain(int index, SongChain chain)
    {
        var c = chain.Normalized();
        lock (_gate)
        {
            if ((uint)index >= _chains.Count) return false;
            if (_chains[index].ValueEquals(c)) return false;
            _chains[index] = c;
        }
        MarkDirty();
        return true;
    }

    public int AddChain(string name)
    {
        int idx;
        lock (_gate) { _chains.Add(new SongChain { Name = string.IsNullOrWhiteSpace(name) ? "Chain" : name.Trim() }); idx = _chains.Count - 1; }
        MarkDirty();
        return idx;
    }

    /// <summary>Removes a chain; the main song chain (index 0) cannot be removed.</summary>
    public bool RemoveChain(int index)
    {
        lock (_gate) { if (index <= 0 || index >= _chains.Count) return false; _chains.RemoveAt(index); }
        MarkDirty();
        return true;
    }

    /// <summary>Loader path: replaces all chains (at least one) without touching the dirty flag.</summary>
    internal void LoadChains(IEnumerable<SongChain> chains)
    {
        var list = chains.Select(c => c.Normalized()).ToList();
        if (list.Count == 0) list.Add(new SongChain());
        lock (_gate) { _chains.Clear(); _chains.AddRange(list); }
    }

    internal void LoadMeta(string name, ProjectSettings settings)
    {
        lock (_gate) { _name = NormalizeName(name); _settings = settings.Normalized(); }
    }
}
