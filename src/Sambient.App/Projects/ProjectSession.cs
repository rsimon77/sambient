using Sambient.App.Settings;
using Sambient.Control;
using Sambient.Seq;

namespace Sambient.App.Projects;

/// <summary>Folders the project machinery works in (injectable so tests never touch the real user folders).</summary>
public sealed record ProjectLocations(string DataDir, string ProjectsDir)
{
    public static ProjectLocations FromEnvironment() => new(AppDataPaths.Directory, AppDataPaths.ProjectsDirectory);
    public string DefaultProjectPath => Path.Combine(ProjectsDir, "Default" + ProjectFile.Extension);
    public string LegacyPatternsDir => ProjectMigration.LegacyDirectory(DataDir);
}

/// <summary>What <see cref="ProjectSession.Start"/> did.</summary>
public sealed record StartupResult(string Source, int MigratedPalettes, string? Message)
{
    /// <summary>"last", "default", "migrated", "new" or "new-unsaved".</summary>
    public bool Migrated => Source == "migrated";
}

/// <summary>
/// The open project and its file operations, without any UI: startup resolution (last project / Default / migration of the old
/// per-slot palette files), New / Open / Save / Save As / Revert, the recent list, autosave and crash recovery. Every operation
/// that replaces the project goes through <see cref="PatternBank.SetProject"/>, so listeners hook <see cref="PatternBank.ProjectReplaced"/>.
/// </summary>
public sealed class ProjectSession
{
    private readonly AppSettings _settings;
    private long _lastAutosaveMs = -1;
    private Project _watched;

    public PatternBank Bank { get; }
    public ProjectLocations Locations { get; }
    public AutosaveStore Autosave { get; }
    /// <summary>Called right before a save: the owner commits live (not yet SAVEd) palette edits into the project.</summary>
    public Action? BeforeSave { get; set; }
    /// <summary>Raised (possibly from any thread) when the project's name, path or dirty state changed, or a recent entry was added.</summary>
    public event Action? StateChanged;
    /// <summary>Autosave interval; the owner calls <see cref="TryAutosave"/> from its timer.</summary>
    public long AutosaveIntervalMs { get; set; } = 120_000;

    public ProjectSession(PatternBank bank, AppSettings settings, ProjectLocations locations)
    {
        Bank = bank; _settings = settings; Locations = locations;
        Autosave = new AutosaveStore(locations.DataDir);
        _watched = bank.Project;
        _watched.Changed += OnProjectChanged;
        bank.ProjectReplaced += () =>
        {
            _watched.Changed -= OnProjectChanged;
            _watched = Bank.Project;
            _watched.Changed += OnProjectChanged;
            StateChanged?.Invoke();
        };
    }

    private void OnProjectChanged() => StateChanged?.Invoke();

    public Project Project => Bank.Project;
    public bool IsDirty => Project.IsDirty;
    public string DisplayName => Project.Name;

    /// <summary>"Sambient — Name", with a bullet when there are unsaved changes.</summary>
    public string WindowTitle => $"Sambient — {Project.Name}{(IsDirty ? " •" : "")}";

    public IReadOnlyList<string> Recent
    {
        get
        {
            var pruned = RecentProjects.Prune(_settings.RecentProjects);
            if (!pruned.SequenceEqual(_settings.RecentProjects)) { _settings.RecentProjects = pruned; _settings.Save(); }
            return pruned;
        }
    }

    private ProjectSettings LegacySettings() => new()
    {
        BpmMode = _settings.BpmGlobal ? BpmMode.Global : BpmMode.Pattern,
        ResetReverbOnChange = _settings.ResetReverbOnChange,
        MetronomeVolume = _settings.MetronomeVolume,
        PreCount = _settings.PreCount,
        MasterTuneCents = _settings.MasterTuneCents,
    };

    private void Remember(string path)
    {
        _settings.RecentProjects = RecentProjects.Add(_settings.RecentProjects, path);
        _settings.LastProjectPath = path;
        _settings.Save();
    }

    // ------------------------------------------------------------ startup

    /// <summary>
    /// Opens the last project; if it is gone the Default project (~/Documents/Sambient/Default.sambient); if that does not exist
    /// either, migrates the old per-slot palette files (when there are any) or creates a fresh Default - written to disk at once.
    /// Never throws; <see cref="StartupResult.Message"/> reports a problem worth showing.
    /// </summary>
    public StartupResult Start()
    {
        string? problem = null;
        var last = _settings.LastProjectPath;
        if (!string.IsNullOrWhiteSpace(last) && File.Exists(last))
        {
            var r = ProjectFile.Load(last);
            if (r.Ok) { Bank.SetProject(r.Project!); Remember(last); return new("last", 0, Warn(r)); }
            problem = r.Error;
        }
        string def = Locations.DefaultProjectPath;
        if (File.Exists(def))
        {
            var r = ProjectFile.Load(def);
            if (r.Ok) { Bank.SetProject(r.Project!); Remember(def); return new("default", 0, problem ?? Warn(r)); }
            Bank.SetProject(Project.CreateNew("Default", LegacySettings()));
            return new("new-unsaved", 0, problem ?? r.Error);
        }
        string legacy = Locations.LegacyPatternsDir;
        Project p;
        int imported = 0;
        bool migrated = ProjectMigration.HasLegacyPatterns(legacy);
        p = migrated ? ProjectMigration.CreateFromLegacy(legacy, LegacySettings(), out imported) : Project.CreateNew("Default", LegacySettings());
        string? err = ProjectFile.Save(p, def);
        Bank.SetProject(p);
        if (err == null) { Remember(def); return new(migrated ? "migrated" : "new", imported, problem); }
        return new("new-unsaved", imported, $"Could not write {def}: {err}");
    }

    private static string? Warn(ProjectLoadResult r) => r.Warnings.Count == 0 ? null : string.Join(" ", r.Warnings);

    // ------------------------------------------------------------ commands

    /// <summary>A new, empty project (factory presets + INIT banks). The caller has already dealt with unsaved changes.</summary>
    public void New()
    {
        Autosave.Clear();
        Bank.SetProject(Project.CreateNew("Untitled", LegacySettings() with { MasterTuneCents = 0 }));
    }

    /// <summary>Opens a project file; null on success, otherwise the error (the current project stays open).</summary>
    public string? Open(string path)
    {
        var r = ProjectFile.Load(path);
        if (!r.Ok)
        {
            _settings.RecentProjects = RecentProjects.Remove(_settings.RecentProjects, path);
            _settings.Save();
            StateChanged?.Invoke();
            return r.Error;
        }
        Autosave.Clear();
        Bank.SetProject(r.Project!);
        Remember(Path.GetFullPath(path));
        return null;
    }

    /// <summary>Saves to the project's own file. Returns null on success, "no path" when it was never saved, or the error text.</summary>
    public string? Save()
    {
        var p = Project;
        if (p.FilePath == null) return NoPath;
        BeforeSave?.Invoke();
        var err = ProjectFile.Save(p, p.FilePath);
        if (err == null) { Autosave.Clear(); Remember(p.FilePath); }
        return err;
    }

    public const string NoPath = "no path";

    public string? SaveAs(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectFile.Extension;
        var p = Project;
        string stem = Path.GetFileNameWithoutExtension(path);
        // the project takes the file's name when it was still untitled or named after its previous file
        if (p.Name == "Untitled" || (p.FilePath != null && p.Name == Path.GetFileNameWithoutExtension(p.FilePath))) p.Rename(stem);
        BeforeSave?.Invoke();
        var err = ProjectFile.Save(p, path);
        if (err == null) { Autosave.Clear(); Remember(Path.GetFullPath(path)); }
        return err;
    }

    public bool CanRevert => Project.FilePath is { } f && File.Exists(f);

    /// <summary>Reloads the project from its file, discarding every change. Null on success, otherwise the error.</summary>
    public string? Revert()
    {
        var path = Project.FilePath;
        if (path == null || !File.Exists(path)) return "The project has no saved version.";
        var r = ProjectFile.Load(path);
        if (!r.Ok) return r.Error;
        Autosave.Clear();
        Bank.SetProject(r.Project!);
        return null;
    }

    // ------------------------------------------------------------ autosave / recovery

    /// <summary>
    /// Writes the autosave when the project is dirty and <see cref="AutosaveIntervalMs"/> has passed. <paramref name="overlay"/> is the
    /// live palette edit (captured by the caller on the UI thread). The write itself runs on a pool thread.
    /// </summary>
    public Task? TryAutosave(long nowMs, Func<(int Index, PatternPalette Palette)?>? overlay = null)
    {
        if (!IsDirty) { _lastAutosaveMs = nowMs; return null; }
        if (_lastAutosaveMs < 0) { _lastAutosaveMs = nowMs; return null; }
        if (nowMs - _lastAutosaveMs < AutosaveIntervalMs) return null;
        _lastAutosaveMs = nowMs;
        var ov = overlay?.Invoke();
        var p = Project;
        return Task.Run(() => Autosave.Write(p, ov));
    }

    /// <summary>An autosave that is newer than its project (or belongs to a never-saved project): offer to restore it.</summary>
    public AutosaveInfo? FindRecovery() => Autosave.FindRecoverable();

    /// <summary>Loads the autosave as the current project (dirty, bound to the original path). Null on success, otherwise the error.</summary>
    public string? RestoreRecovery()
    {
        var info = Autosave.ReadInfo();
        var r = Autosave.Load();
        if (!r.Ok) { Autosave.Clear(); return r.Error; }
        var p = r.Project!;
        p.FilePath = info?.ProjectPath;
        p.MarkDirty();
        Bank.SetProject(p);
        return null;
    }

    public void DiscardRecovery() => Autosave.Clear();
}
