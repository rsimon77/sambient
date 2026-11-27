namespace Sambient.Control;

/// <summary>
/// Location of Sambient's per-user files: the app-data folder (settings.json, midimap.json, autosave) and the default folder of
/// the project files. Test / harness hooks: the environment variables SAMBIENT_DATA_DIR and SAMBIENT_PROJECTS_DIR override them.
/// </summary>
public static class AppDataPaths
{
    public const string DataDirVariable = "SAMBIENT_DATA_DIR", ProjectsDirVariable = "SAMBIENT_PROJECTS_DIR";

    /// <summary>~/Library/Application Support/Sambient on macOS (note: .NET's ApplicationData would be ~/.config there).</summary>
    public static string Directory { get; } = Resolve();

    /// <summary>Default folder of the .sambient project files: ~/Documents/Sambient.</summary>
    public static string ProjectsDirectory { get; } = ResolveProjects();

    public static string File(string name) => Path.Combine(Directory, name);

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);
        string baseDir = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(baseDir, "Sambient");
    }

    private static string ResolveProjects()
    {
        var overridden = Environment.GetEnvironmentVariable(ProjectsDirVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Sambient");
    }
}
