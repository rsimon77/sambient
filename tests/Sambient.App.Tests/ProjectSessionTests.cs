using Sambient.App.Projects;
using Sambient.App.Settings;
using Sambient.Engine;
using Sambient.Seq;
using Xunit;

namespace Sambient.App.Tests;

/// <summary>A throwaway "user home": data dir, projects dir and a settings file, all under a temp folder.</summary>
internal sealed class Sandbox : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "sambient-sess-" + Guid.NewGuid().ToString("N"));
    public readonly ProjectLocations Loc;
    public readonly AppSettings Settings;
    public readonly PatternBank Bank = new();
    public readonly ProjectSession Session;

    public Sandbox()
    {
        Loc = new ProjectLocations(Path.Combine(Root, "data"), Path.Combine(Root, "Documents", "Sambient"));
        Directory.CreateDirectory(Root);
        Settings = new AppSettings { FilePath = Path.Combine(Root, "settings.json") };
        Session = new ProjectSession(Bank, Settings, Loc);
    }

    public string PathOf(string name) => Path.Combine(Loc.ProjectsDir, name + ProjectFile.Extension);

    public ProjectSession Fresh() => new(new PatternBank(), Settings, Loc);

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

public class ProjectSessionTests
{
    [Fact]
    public void FreshInstallCreatesAndOpensDefault()
    {
        using var sb = new Sandbox();
        var r = sb.Session.Start();
        Assert.Equal("new", r.Source);
        Assert.True(File.Exists(sb.Loc.DefaultProjectPath));
        Assert.Equal("Default", sb.Session.Project.Name);
        Assert.False(sb.Session.IsDirty);
        Assert.Equal(sb.Loc.DefaultProjectPath, sb.Settings.LastProjectPath);
        Assert.Equal("Sambient — Default", sb.Session.WindowTitle);
    }

    [Fact]
    public void FirstLaunchMigratesTheOldPaletteFilesAndLeavesTheFolderAlone()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(sb.Loc.LegacyPatternsDir);
        var mine = FactoryPresets.Get(1, 4)!; mine.Name = "OLDSAVE"; mine.SetLayer(1, ParamId.Cutoff, 33);
        var legacyFile = Path.Combine(sb.Loc.LegacyPatternsDir, "B2-05.json");
        File.WriteAllText(legacyFile, PatternJson.Serialize(mine));
        File.WriteAllText(Path.Combine(sb.Loc.LegacyPatternsDir, "B6-02.json"), PatternJson.Serialize(new PatternPalette("USER")));
        sb.Settings.MetronomeVolume = 5; sb.Settings.BpmGlobal = true; sb.Settings.MasterTuneCents = -20;   // pre-project app settings
        string legacyText = File.ReadAllText(legacyFile);

        var r = sb.Session.Start();

        Assert.Equal("migrated", r.Source);
        Assert.Equal(2, r.MigratedPalettes);
        Assert.True(File.Exists(sb.Loc.DefaultProjectPath));
        Assert.Equal(legacyText, File.ReadAllText(legacyFile));
        var p = sb.Session.Project;
        Assert.Equal("OLDSAVE", p.GetName(1, 4).TrimEnd());
        Assert.Equal("USER", p.GetName(5, 1).TrimEnd());
        Assert.Equal(5, p.Settings.MetronomeVolume);
        Assert.Equal(BpmMode.Global, p.Settings.BpmMode);
        Assert.Equal(-20f, p.Settings.MasterTuneCents);

        // it was written to disk: a second app start (new session) opens it as the last project and does not migrate again
        File.WriteAllText(Path.Combine(sb.Loc.LegacyPatternsDir, "B1-01.json"), PatternJson.Serialize(new PatternPalette("LATE")));
        var again = sb.Fresh();
        Assert.Equal("last", again.Start().Source);
        Assert.Equal("OLDSAVE", again.Project.GetName(1, 4).TrimEnd());
        Assert.NotEqual("LATE", again.Project.GetName(0, 0).TrimEnd());
    }

    [Fact]
    public void StartupFallsBackFromAMissingLastProjectToDefault()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        sb.Settings.LastProjectPath = sb.PathOf("vanished");
        var again = sb.Fresh();
        var r = again.Start();
        Assert.Equal("default", r.Source);
        Assert.Equal("Default", again.Project.Name);
    }

    [Fact]
    public void StartupSurvivesACorruptDefaultAndReportsIt()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(sb.Loc.ProjectsDir);
        File.WriteAllText(sb.Loc.DefaultProjectPath, "{ not a project");
        var r = sb.Session.Start();
        Assert.Equal("new-unsaved", r.Source);
        Assert.NotNull(r.Message);
        Assert.Equal("{ not a project", File.ReadAllText(sb.Loc.DefaultProjectPath));   // never overwritten
        Assert.Null(sb.Session.Project.FilePath);
    }

    [Fact]
    public void NewOpenSaveAsRevertAndTheDirtyFlag()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        int changes = 0; sb.Session.StateChanged += () => changes++;

        sb.Session.New();
        Assert.Equal("Untitled", sb.Session.Project.Name);
        Assert.False(sb.Session.IsDirty);
        Assert.Equal(Session_NoPath, sb.Session.Save());                           // never saved: the caller must ask for a path

        sb.Bank.Save(4, 0, Named("FIRST"));
        Assert.True(sb.Session.IsDirty);
        Assert.EndsWith("•", sb.Session.WindowTitle);

        int before = 0; sb.Session.BeforeSave = () => before++;
        string path = sb.PathOf("MySong");
        Assert.Null(sb.Session.SaveAs(path));
        Assert.Equal(1, before);
        Assert.False(sb.Session.IsDirty);
        Assert.Equal("MySong", sb.Session.Project.Name);                           // the untitled project took the file's name
        Assert.Equal(Path.GetFullPath(path), sb.Settings.RecentProjects[0]);
        Assert.False(sb.Session.CanRevert && sb.Session.IsDirty);

        sb.Bank.Save(4, 0, Named("SECOND"));
        Assert.True(sb.Session.IsDirty);
        Assert.Null(sb.Session.Save());
        Assert.Equal(2, before);
        Assert.False(sb.Session.IsDirty);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".bak")));              // the previous version

        sb.Bank.Save(4, 0, Named("THIRD"));
        Assert.True(sb.Session.CanRevert);
        Assert.Null(sb.Session.Revert());
        Assert.Equal("SECOND", sb.Bank.GetName(4, 0).TrimEnd());                   // back to the saved state
        Assert.False(sb.Session.IsDirty);

        Assert.True(changes > 0);
    }

    private const string Session_NoPath = ProjectSession.NoPath;

    private static PatternPalette Named(string n) { var p = new PatternPalette(n); p.Tracks[0].Steps[0].Notes.Add(StepNoteData.Make(60)); return p; }

    [Fact]
    public void SaveAsAddsTheExtensionAndPersistsTheRecentList()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        sb.Session.New();
        Assert.Null(sb.Session.SaveAs(Path.Combine(sb.Loc.ProjectsDir, "noext")));
        Assert.True(File.Exists(sb.PathOf("noext")));
        var reloaded = AppSettings.Load(sb.Settings.FilePath);                      // settings.json holds last project + recents
        Assert.Equal(Path.GetFullPath(sb.PathOf("noext")), reloaded.LastProjectPath);
        Assert.Contains(Path.GetFullPath(sb.PathOf("noext")), reloaded.RecentProjects);
    }

    [Fact]
    public void RecentListIsMaintainedAndMissingFilesDisappear()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        for (int i = 0; i < 12; i++)
        {
            sb.Session.New();
            Assert.Null(sb.Session.SaveAs(sb.PathOf("p" + i)));
        }
        Assert.Equal(10, sb.Session.Recent.Count);
        Assert.Equal(Path.GetFullPath(sb.PathOf("p11")), sb.Session.Recent[0]);
        Assert.Null(sb.Session.Open(sb.PathOf("p5")));                              // re-opening moves it to the front
        Assert.Equal(Path.GetFullPath(sb.PathOf("p5")), sb.Session.Recent[0]);
        File.Delete(sb.PathOf("p9"));
        Assert.DoesNotContain(Path.GetFullPath(sb.PathOf("p9")), sb.Session.Recent);
        Assert.Equal(9, sb.Session.Recent.Count);
    }

    [Fact]
    public void OpeningAnUnreadableFileFailsCleanlyAndKeepsTheCurrentProject()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        sb.Bank.Save(4, 0, Named("KEEP"));
        string bad = sb.PathOf("bad");
        File.WriteAllText(bad, "garbage");
        sb.Settings.RecentProjects = [bad];
        var err = sb.Session.Open(bad);
        Assert.NotNull(err);
        Assert.Equal("KEEP", sb.Bank.GetName(4, 0).TrimEnd());
        Assert.True(sb.Session.IsDirty);
        Assert.Empty(sb.Settings.RecentProjects);
    }

    [Fact]
    public async Task AutosaveRunsOnlyWhenDirtyAndDue()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        sb.Session.AutosaveIntervalMs = 1000;
        Assert.Null(sb.Session.TryAutosave(0));                                     // clean: nothing to do
        sb.Bank.Save(4, 0, Named("AUTO"));
        Assert.Null(sb.Session.TryAutosave(500));                                   // dirty but not due (first dirty tick starts the clock)
        Assert.Null(sb.Session.TryAutosave(900));
        var t = sb.Session.TryAutosave(1600, () => (PatternBank.SlotIndex(5, 5), Named("LIVE")));
        Assert.NotNull(t);
        await t!;
        Assert.True(File.Exists(sb.Session.Autosave.ProjectFilePath));
        Assert.Equal("LIVE", sb.Session.Autosave.Load().Project!.GetName(5, 5).TrimEnd());
        Assert.Null(sb.Session.TryAutosave(1700));                                  // not due again yet
        Assert.NotNull(sb.Session.TryAutosave(2700));
        Assert.Null(sb.Session.Save());                                             // saving clears the autosave
        Assert.False(File.Exists(sb.Session.Autosave.ProjectFilePath));
    }

    [Fact]
    public async Task CrashRecoveryOffersTheAutosaveAndRestoresItDirtyAndBoundToItsFile()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        sb.Bank.Save(4, 0, Named("SAVED"));
        Assert.Null(sb.Session.Save());                                             // Default.sambient with "SAVED"
        File.SetLastWriteTimeUtc(sb.Loc.DefaultProjectPath, DateTime.UtcNow.AddMinutes(-10));
        sb.Bank.Save(4, 0, Named("UNSAVED"));
        sb.Session.AutosaveIntervalMs = 0;
        _ = sb.Session.TryAutosave(0);
        await sb.Session.TryAutosave(1)!;                                           // ... the app crashes here

        var next = sb.Fresh();                                                      // next launch
        next.Start();
        Assert.Equal("SAVED", next.Bank.GetName(4, 0).TrimEnd());
        var rec = next.FindRecovery();
        Assert.NotNull(rec);
        Assert.Null(next.RestoreRecovery());
        Assert.Equal("UNSAVED", next.Bank.GetName(4, 0).TrimEnd());
        Assert.True(next.IsDirty);
        Assert.Equal(Path.GetFullPath(sb.Loc.DefaultProjectPath), next.Project.FilePath);

        var third = sb.Fresh();
        third.Start();
        third.DiscardRecovery();
        Assert.Null(third.FindRecovery());
    }

    [Fact]
    public async Task NoRecoveryWhenTheProjectWasSavedAfterTheAutosave()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        sb.Bank.Save(4, 0, Named("X"));
        sb.Session.AutosaveIntervalMs = 0;
        _ = sb.Session.TryAutosave(0); await sb.Session.TryAutosave(1)!;
        File.SetLastWriteTimeUtc(sb.Loc.DefaultProjectPath, DateTime.UtcNow.AddMinutes(5));
        Assert.Null(sb.Fresh().FindRecovery());
    }

    [Fact]
    public void SettingsAndChainEditsMakeTheProjectDirtyAndSurviveSave()
    {
        using var sb = new Sandbox();
        sb.Session.Start();
        var p = sb.Session.Project;
        p.SetSettings(p.Settings with { MetronomeVolume = 2, PreCount = 0 });
        var c = p.Song; c.Entries.Add(new ChainSlot(0, 5)); c.Loop = true; c.Relaxation = true; c.Repeats = 6; c.FadeSeconds = 20;
        p.SetChain(0, c);
        Assert.True(sb.Session.IsDirty);
        Assert.Null(sb.Session.Save());
        Assert.False(sb.Session.IsDirty);
        var q = ProjectFile.Load(sb.Loc.DefaultProjectPath).Project!;
        Assert.Equal(2, q.Settings.MetronomeVolume);
        Assert.Equal(0, q.Settings.PreCount);
        var qc = q.Song;
        Assert.Equal([new ChainSlot(0, 5)], qc.Entries);
        Assert.True(qc.Loop && qc.Relaxation);
        Assert.Equal(6, qc.Repeats);
        Assert.Equal(20, qc.FadeSeconds);
    }
}
