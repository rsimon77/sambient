using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sambient.Seq;

/// <summary>The "Open Recent" list: most recent first, de-duplicated, at most <see cref="Max"/> entries, missing files removed.</summary>
public static class RecentProjects
{
    public const int Max = 10;

    private static string Norm(string p) { try { return Path.GetFullPath(p); } catch (Exception) { return p; } }

    /// <summary>Puts <paramref name="path"/> first (removing an older entry of the same file) and trims to <see cref="Max"/>.</summary>
    public static List<string> Add(IEnumerable<string> list, string path)
    {
        string p = Norm(path);
        var r = new List<string> { p };
        foreach (var e in list)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            string n = Norm(e);
            if (!r.Contains(n, StringComparer.OrdinalIgnoreCase)) r.Add(n);
        }
        return r.Take(Max).ToList();
    }

    public static List<string> Remove(IEnumerable<string> list, string path)
    {
        string p = Norm(path);
        return list.Where(e => !string.Equals(Norm(e), p, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Drops entries whose file no longer exists (and de-duplicates / trims).</summary>
    public static List<string> Prune(IEnumerable<string> list, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var r = new List<string>();
        foreach (var e in list)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            string n = Norm(e);
            if (exists(n) && !r.Contains(n, StringComparer.OrdinalIgnoreCase)) r.Add(n);
        }
        return r.Take(Max).ToList();
    }
}

/// <summary>What the autosave file belongs to.</summary>
public sealed record AutosaveInfo(string? ProjectPath, string ProjectName, DateTime SavedUtc);

/// <summary>
/// Crash recovery: "autosave.sambient" (a full project file) + "autosave.json" (which project path it belongs to and when it was
/// written) in the app-data folder. Written while the project is dirty, cleared by every save / discard / clean exit.
/// </summary>
public sealed class AutosaveStore
{
    public string Directory { get; }
    public string ProjectFilePath => Path.Combine(Directory, "autosave.sambient");
    public string MetaPath => Path.Combine(Directory, "autosave.json");

    public AutosaveStore(string directory) { Directory = directory; }

    /// <summary>Writes the autosave (best effort; returns false on I/O errors). <paramref name="overlay"/>: live palette edit to include.</summary>
    public bool Write(Project p, (int Index, PatternPalette Palette)? overlay = null, DateTime? nowUtc = null)
    {
        try
        {
            string json = ProjectFile.Serialize(p, overlay);
            ProjectFile.WriteAtomic(ProjectFilePath, json, keepBackup: false);
            var meta = JsonSerializer.Serialize(new { projectPath = p.FilePath, projectName = p.Name, savedUtc = (nowUtc ?? DateTime.UtcNow).ToString("o", CultureInfo.InvariantCulture) });
            ProjectFile.WriteAtomic(MetaPath, meta, keepBackup: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>The autosave's metadata, or null when there is none (or it is unreadable).</summary>
    public AutosaveInfo? ReadInfo()
    {
        try
        {
            if (!File.Exists(ProjectFilePath) || !File.Exists(MetaPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(MetaPath));
            var r = doc.RootElement;
            string? path = r.TryGetProperty("projectPath", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
            string name = r.TryGetProperty("projectName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() ?? "Untitled" : "Untitled";
            DateTime saved = File.GetLastWriteTimeUtc(ProjectFilePath);
            if (r.TryGetProperty("savedUtc", out var su) && su.ValueKind == JsonValueKind.String
                && DateTime.TryParse(su.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) saved = d.ToUniversalTime();
            return new AutosaveInfo(path, name, saved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Recovery rule: an autosave is worth offering when its project was never saved (no path), the project file has vanished, or
    /// the autosave is newer than the project file (the project was edited after its last save and the app did not exit cleanly).
    /// </summary>
    public static bool IsRecoverable(AutosaveInfo info, DateTime? projectFileWriteUtc)
        => info.ProjectPath == null || projectFileWriteUtc == null || info.SavedUtc > projectFileWriteUtc.Value.AddSeconds(1);

    /// <summary>The recoverable autosave, or null.</summary>
    public AutosaveInfo? FindRecoverable()
    {
        var info = ReadInfo();
        if (info == null) return null;
        DateTime? fileTime = null;
        if (info.ProjectPath != null && File.Exists(info.ProjectPath)) fileTime = File.GetLastWriteTimeUtc(info.ProjectPath);
        return IsRecoverable(info, fileTime) ? info : null;
    }

    public ProjectLoadResult Load() => ProjectFile.Load(ProjectFilePath);

    public void Clear()
    {
        foreach (var f in new[] { ProjectFilePath, MetaPath, ProjectFilePath + ".tmp", MetaPath + ".tmp" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// One-time migration of the pre-project storage (one JSON file per user palette, "B5-03.json" = bank 5 slot 3 in
/// ~/Library/Application Support/Sambient/patterns) into a project. The old folder is only read, never modified.
/// </summary>
public static partial class ProjectMigration
{
    public static string LegacyDirectory(string appDataDirectory) => Path.Combine(appDataDirectory, "patterns");

    [GeneratedRegex(@"^B([1-8])-(0[1-9]|1[0-6])\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyName();

    private static IEnumerable<(int Bank, int Slot, string Path)> LegacyFiles(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) yield break;
        foreach (var f in System.IO.Directory.EnumerateFiles(dir, "B*-*.json"))
        {
            var m = LegacyName().Match(Path.GetFileName(f));
            if (m.Success) yield return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1, f);
        }
    }

    public static bool HasLegacyPatterns(string legacyDir) => LegacyFiles(legacyDir).Any();

    /// <summary>A project made of the factory presets overridden by the legacy user palette files. <paramref name="imported"/> counts the overrides.</summary>
    public static Project CreateFromLegacy(string legacyDir, ProjectSettings settings, out int imported, string name = "Default")
    {
        var p = Project.CreateNew(name, settings);
        imported = 0;
        foreach (var (bank, slot, path) in LegacyFiles(legacyDir))
        {
            try
            {
                var pal = PatternJson.TryParse(File.ReadAllText(path));
                if (pal == null) continue;
                p.LoadSlot(bank, slot, pal);
                imported++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return p;
    }
}
