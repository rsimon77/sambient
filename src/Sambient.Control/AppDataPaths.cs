namespace Sambient.Control;

/// <summary>Location of Sambient's per-user files (settings.json, midimap.json).</summary>
public static class AppDataPaths
{
    /// <summary>~/Library/Application Support/Sambient on macOS (note: .NET's ApplicationData would be ~/.config there).</summary>
    public static string Directory { get; } = Resolve();

    public static string File(string name) => Path.Combine(Directory, name);

    private static string Resolve()
    {
        string baseDir = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(baseDir, "Sambient");
    }
}
