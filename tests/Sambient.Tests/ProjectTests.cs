using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.Tests;

internal static class ProjectTestUtil
{
    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "sambient-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A project where a spread of slots has notes, probabilities, locks and parameter changes.</summary>
    public static Project RichProject()
    {
        var p = Project.CreateNew("Evening Set");
        for (int i = 0; i < Project.SlotCount; i += 3)
        {
            int b = i / 16, s = i % 16;
            var pal = p.GetPalette(b, s);
            pal.Name = $"P{i:000}";
            pal.PatternBars = 1 + i % 9;
            pal.SetLayer(i % 4, ParamId.Cutoff, 10 + i % 100);
            pal.SetLayer(0, ParamId.Harmonic, 3 + i % 120);
            pal.SetGlobal(GlobalParamId.Bpm, 60 + i % 80);
            pal.Tracks[i % 4].Steps[i % 64].Notes.Add(StepNoteData.Make(40 + i % 40, 50 + i % 70, 1 + i % 4));
            pal.Tracks[i % 4].Steps[i % 64].Probability = 25 + (i * 7) % 76;
            pal.Tracks[(i + 1) % 4].Steps[(i * 5) % 64].Locks[ParamId.Harmonic] = 5 + i % 100;
            pal.Tracks[(i + 1) % 4].Steps[(i * 5) % 64].Notes.Add(StepNoteData.Make(60));
            p.SetPalette(b, s, pal);
        }
        var song = new SongChain { Name = "Song", Loop = true, Repeats = 4, Relaxation = true, FadeSeconds = 15, FadeInSeconds = 5 };
        song.Entries.AddRange([new ChainSlot(0, 3), new ChainSlot(4, 0), new ChainSlot(7, 15)]);
        p.SetChain(0, song);
        int extra = p.AddChain("Alt");
        p.SetChain(extra, new SongChain { Name = "Alt", Repeats = 2, Entries = [new ChainSlot(1, 1)] });
        p.SetSettings(new ProjectSettings { BpmMode = BpmMode.Global, ResetReverbOnChange = true, MuteMode = MuteMode.Sequence, MetronomeVolume = 11, PreCount = 4, MasterTuneCents = -12.5f });
        p.SamplesJson = "{\"S01\":{\"name\":\"rain\",\"rate\":48000}}";
        return p;
    }
}

public class ProjectModelTests
{
    [Fact]
    public void NewProjectHoldsFactoryPresetsAndInitBanks()
    {
        var p = Project.CreateNew();
        Assert.False(p.IsDirty);
        for (int b = 0; b < 8; b++)
            for (int s = 0; s < 16; s++)
            {
                Assert.False(p.IsModified(b, s));
                var pal = p.GetPalette(b, s);
                if (b < 4) PatternJsonTests.AssertSame(FactoryPresets.Get(b, s)!, pal);
                else Assert.Equal("INIT", pal.Name.TrimEnd());
                Assert.Equal(pal.Name, p.GetName(b, s));
            }
        Assert.Equal(1, p.ChainCount);
        Assert.Equal("Song", p.Song.Name);
    }

    [Fact]
    public void DirtyFlagTransitions()
    {
        var p = Project.CreateNew();
        int events = 0; p.Changed += () => events++;
        Assert.False(p.IsDirty);

        var pal = p.GetPalette(4, 0);
        Assert.False(p.SetPalette(4, 0, pal));                     // unchanged content: not dirty
        Assert.False(p.IsDirty);
        pal.Name = "EDIT"; pal.Tracks[0].Steps[0].Notes.Add(StepNoteData.Make(60));
        Assert.True(p.SetPalette(4, 0, pal));
        Assert.True(p.IsDirty);
        Assert.True(p.IsModified(4, 0));
        Assert.True(events > 0);

        p.MarkClean();
        Assert.False(p.IsDirty);

        Assert.False(p.SetSettings(p.Settings));                   // same settings
        Assert.False(p.IsDirty);
        Assert.True(p.SetSettings(p.Settings with { PreCount = 5 }));
        Assert.True(p.IsDirty); p.MarkClean();

        var song = p.Song;
        Assert.False(p.SetChain(0, song));                         // same chain
        Assert.False(p.IsDirty);
        song.Entries.Add(new ChainSlot(0, 0));
        Assert.True(p.SetChain(0, song));
        Assert.True(p.IsDirty); p.MarkClean();

        Assert.False(p.Rename("Untitled"));
        Assert.True(p.Rename("Fresh Name"));
        Assert.True(p.IsDirty); p.MarkClean();

        Assert.True(p.RevertSlot(4, 0));                           // back to INIT
        Assert.True(p.IsDirty); p.MarkClean();
        Assert.False(p.RevertSlot(4, 0));                          // nothing to revert
        Assert.False(p.IsDirty);

        Assert.True(p.InitSlot(0, 0));                             // init over a factory preset
        Assert.True(p.IsDirty); p.MarkClean();
        Assert.True(p.RevertSlot(0, 0));                           // revert to factory
        Assert.False(p.IsModified(0, 0));
        Assert.True(p.IsDirty);

        p.MarkClean();
        Assert.True(p.AddChain("Two") > 0); Assert.True(p.IsDirty); p.MarkClean();
        Assert.False(p.RemoveChain(0));                            // the song chain stays
        Assert.True(p.RemoveChain(1)); Assert.True(p.IsDirty);
    }

    [Fact]
    public void ChainsAreNormalizedAndHandedOutAsCopies()
    {
        var p = Project.CreateNew();
        var c = new SongChain { Repeats = 500, Relaxation = false, FadeSeconds = 17, Entries = [new ChainSlot(9, 0), new ChainSlot(2, 3)] };
        p.SetChain(0, c);
        var got = p.Song;
        Assert.Equal(99, got.Repeats);
        Assert.Equal(15, got.FadeSeconds);                         // snapped to 5 s steps
        Assert.Equal([new ChainSlot(2, 3)], got.Entries);          // the invalid entry is dropped
        got.Entries.Clear();                                       // editing the copy does not touch the project
        Assert.Single(p.Song.Entries);
        p.SetChain(0, new SongChain { Relaxation = true, Repeats = 1 });
        Assert.Equal(2, p.Song.Repeats);                           // relaxation: 2..10
    }

    [Fact]
    public void FingerprintDetectsEveryKindOfEdit()
    {
        var a = FactoryPresets.Get(0, 0)!;
        ulong f = PatternFingerprint.Of(a);
        Assert.Equal(f, PatternFingerprint.Of(a.Clone()));
        var edits = new List<Action<PatternPalette>>
        {
            p => p.Name = "OTHER", p => p.PatternBars = 7, p => p.SetLayer(2, ParamId.Resonance, 99), p => p.SetGlobal(GlobalParamId.Bpm, 99),
            p => p.Tracks[3].Steps[63].Notes.Add(StepNoteData.Make(50)), p => p.Tracks[0].Steps[10].Probability = 50,
            p => p.Tracks[1].Steps[2].Locks[ParamId.Cutoff] = 12,
        };
        foreach (var e in edits) { var c = a.Clone(); e(c); Assert.NotEqual(f, PatternFingerprint.Of(c)); }
    }
}

public class ProjectFileTests
{
    [Fact]
    public void RoundTripKeepsAll128PalettesChainsAndSettings()
    {
        var dir = ProjectTestUtil.TempDir();
        try
        {
            var p = ProjectTestUtil.RichProject();
            string path = Path.Combine(dir, "rich.sambient");
            Assert.Null(ProjectFile.Save(p, path));
            Assert.False(p.IsDirty);
            Assert.Equal(Path.GetFullPath(path), p.FilePath);

            var r = ProjectFile.Load(path);
            Assert.True(r.Ok, r.Error);
            Assert.Empty(r.Warnings);
            var q = r.Project!;
            Assert.False(q.IsDirty);
            Assert.Equal(p.Name, q.Name);
            Assert.Equal(p.Created.ToUnixTimeSeconds(), q.Created.ToUnixTimeSeconds());
            for (int b = 0; b < 8; b++)
                for (int s = 0; s < 16; s++)
                {
                    PatternJsonTests.AssertSame(p.GetPalette(b, s), q.GetPalette(b, s));
                    Assert.Equal(p.IsModified(b, s), q.IsModified(b, s));
                }
            Assert.Equal(p.Settings, q.Settings);
            Assert.Equal(2, q.ChainCount);
            for (int i = 0; i < 2; i++) Assert.True(p.GetChain(i).ValueEquals(q.GetChain(i)));
            Assert.Equal("rain", JsonDocument.Parse(q.SamplesJson!).RootElement.GetProperty("S01").GetProperty("name").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FileIsVersionedJsonWithTheDocumentedShape()
    {
        var json = ProjectFile.Serialize(ProjectTestUtil.RichProject());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("sambient-project", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(128, root.GetProperty("palettes").GetArrayLength());
        Assert.Equal("sambient-pattern", root.GetProperty("palettes")[0].GetProperty("format").GetString());
        Assert.Equal("Global", root.GetProperty("settings").GetProperty("bpmMode").GetString());
        Assert.Equal(3, root.GetProperty("chains")[0].GetProperty("entries").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("samples").ValueKind);
        Assert.InRange(json.Length, 100_000, 12_000_000);
    }

    [Fact]
    public void LoadingIsFastEnough()
    {
        var json = ProjectFile.Serialize(ProjectTestUtil.RichProject());
        var sw = Stopwatch.StartNew();
        var r = ProjectFile.Parse(json);
        sw.Stop();
        Assert.True(r.Ok);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"parse took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TolerantLoadIgnoresUnknownFieldsAndDefaultsMissingOnes()
    {
        const string json = """
        { "format": "sambient-project", "version": 1, "futureThing": { "x": [1,2,3] }, "name": "Sparse",
          "settings": { "metronomeVolume": "9", "preCount": 99, "masterTuneCents": 500, "bpmMode": "global", "bogus": true },
          "chains": [ { "name": "", "entries": [ {"bank":0,"slot":1}, {"bank":99,"slot":1}, {"nonsense":1}, "x" ], "repeats": "3", "loop": true, "extra": 1 }, 7 ],
          "palettes": [ null, 5, { "format": "other" }, { "format": "sambient-pattern", "name": "KEEP", "futureField": 1 } ] }
        """;
        var r = ProjectFile.Parse(json);
        Assert.True(r.Ok, r.Error);
        var p = r.Project!;
        Assert.Equal("Sparse", p.Name);
        Assert.Equal(9, p.Settings.MetronomeVolume);
        Assert.Equal(8, p.Settings.PreCount);                      // clamped
        Assert.Equal(75f, p.Settings.MasterTuneCents);             // clamped
        Assert.Equal(BpmMode.Global, p.Settings.BpmMode);
        Assert.Equal(MuteMode.Sound, p.Settings.MuteMode);         // missing -> default
        Assert.Equal(1, p.ChainCount);
        var c = p.Song;
        Assert.Equal("Song", c.Name);
        Assert.Equal([new ChainSlot(0, 1)], c.Entries);
        Assert.Equal(3, c.Repeats);
        Assert.True(c.Loop);
        // missing / unreadable palettes are INIT (also in the factory banks); the readable one is kept
        Assert.Equal("INIT", p.GetPalette(0, 0).Name.TrimEnd());
        Assert.Equal("INIT", p.GetPalette(0, 1).Name.TrimEnd());
        Assert.Equal("INIT", p.GetPalette(0, 2).Name.TrimEnd());
        Assert.Equal("KEEP", p.GetPalette(0, 3).Name.TrimEnd());
        Assert.Equal("INIT", p.GetPalette(7, 15).Name.TrimEnd());
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void ExtraPalettesAreIgnoredAndNewerVersionsWarn()
    {
        var sb = new StringBuilder("{ \"format\": \"sambient-project\", \"version\": 7, \"palettes\": [");
        for (int i = 0; i < 140; i++) sb.Append(i == 0 ? "" : ",").Append("{\"name\":\"P").Append(i).Append("\"}");
        sb.Append("] }");
        var r = ProjectFile.Parse(sb.ToString());
        Assert.True(r.Ok);
        Assert.Contains(r.Warnings, w => w.Contains("newer"));
        Assert.Equal("P127", r.Project!.GetPalette(7, 15).Name.TrimEnd());
        Assert.Equal("P0", r.Project.GetPalette(0, 0).Name.TrimEnd());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"format\": \"sambient-project\", \"palettes\": [ { ")]          // truncated
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{ \"format\": \"sambient-pattern\", \"name\": \"X\" }")]            // a single palette file is not a project
    [InlineData("{ \"format\": 7 }")]
    public void NotAProjectIsAnErrorResultNeverAnException(string text)
    {
        var r = ProjectFile.Parse(text);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void MissingFileIsAnErrorResult()
    {
        var r = ProjectFile.Load(Path.Combine(Path.GetTempPath(), "sambient-nope-" + Guid.NewGuid().ToString("N") + ".sambient"));
        Assert.False(r.Ok);
        Assert.Contains("Cannot read", r.Error);
    }

    [Fact]
    public void SaveIsAtomicAndKeepsTheLastVersionAsBackup()
    {
        var dir = ProjectTestUtil.TempDir();
        try
        {
            string path = Path.Combine(dir, "a.sambient");
            var p = Project.CreateNew("First");
            Assert.Null(ProjectFile.Save(p, path));
            string first = File.ReadAllText(path);
            Assert.False(File.Exists(Path.Combine(dir, "a.bak")));         // nothing to back up yet
            Assert.False(File.Exists(path + ".tmp"));

            p.Rename("Second");
            Assert.True(p.IsDirty);
            Assert.Null(ProjectFile.Save(p, path));
            Assert.False(p.IsDirty);
            Assert.Equal(first, File.ReadAllText(Path.Combine(dir, "a.bak")));
            Assert.Equal("Second", ProjectFile.Load(path).Project!.Name);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FailedSaveLeavesTheOldFileAndTheDirtyFlag()
    {
        var dir = ProjectTestUtil.TempDir();
        try
        {
            string path = Path.Combine(dir, "a.sambient");
            var p = Project.CreateNew("Keep");
            Assert.Null(ProjectFile.Save(p, path));
            string before = File.ReadAllText(path);
            p.Rename("Changed");
            string blocked = Path.Combine(path, "inside.sambient");        // "a.sambient" is a file, so this cannot be created
            var err = ProjectFile.Save(p, blocked);
            Assert.NotNull(err);
            Assert.True(p.IsDirty);
            Assert.Equal(Path.GetFullPath(path), p.FilePath);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SamplesSectionIsPreservedVerbatim()
    {
        var p = Project.CreateNew();
        p.SamplesJson = "{\"later\":{\"a\":[1,2,3]}}";
        var q = ProjectFile.Parse(ProjectFile.Serialize(p)).Project!;
        Assert.Equal(3, JsonDocument.Parse(q.SamplesJson!).RootElement.GetProperty("later").GetProperty("a").GetArrayLength());
        Assert.Null(ProjectFile.Parse(ProjectFile.Serialize(Project.CreateNew())).Project!.SamplesJson);
    }
}

public class ProjectMigrationTests
{
    [Fact]
    public void LegacyPaletteFilesBecomeOverridesOfTheFactoryPresets()
    {
        var dir = ProjectTestUtil.TempDir();
        try
        {
            var legacy = Path.Combine(dir, "patterns");
            Directory.CreateDirectory(legacy);
            var edited = FactoryPresets.Get(0, 2)!; edited.Name = "MINE"; edited.SetLayer(0, ParamId.Cutoff, 5);
            var user = new PatternPalette("USER"); user.Tracks[1].Steps[9].Notes.Add(StepNoteData.Make(55, 90, 2)); user.Tracks[1].Steps[9].Locks[ParamId.Harmonic] = 33;
            File.WriteAllText(Path.Combine(legacy, "B1-03.json"), PatternJson.Serialize(edited));
            File.WriteAllText(Path.Combine(legacy, "B8-16.json"), PatternJson.Serialize(user));
            File.WriteAllText(Path.Combine(legacy, "B6-01.json"), "garbage");                  // unreadable: ignored
            File.WriteAllText(Path.Combine(legacy, "B9-01.json"), PatternJson.Serialize(user)); // not a valid slot
            File.WriteAllText(Path.Combine(legacy, "notes.txt"), "hi");
            var before = Directory.GetFiles(legacy).Select(f => (f, File.ReadAllText(f))).OrderBy(x => x.f).ToList();

            Assert.True(ProjectMigration.HasLegacyPatterns(legacy));
            Assert.False(ProjectMigration.HasLegacyPatterns(Path.Combine(dir, "none")));
            var settings = new ProjectSettings { BpmMode = BpmMode.Global, MetronomeVolume = 3, MasterTuneCents = 7 };
            var p = ProjectMigration.CreateFromLegacy(legacy, settings, out int imported);

            Assert.Equal(2, imported);
            Assert.Equal("Default", p.Name);
            Assert.Equal(settings, p.Settings);
            PatternJsonTests.AssertSame(edited, p.GetPalette(0, 2));
            PatternJsonTests.AssertSame(user, p.GetPalette(7, 15));
            PatternJsonTests.AssertSame(FactoryPresets.Get(0, 3)!, p.GetPalette(0, 3));         // untouched slots stay factory
            Assert.Equal("INIT", p.GetPalette(5, 0).Name.TrimEnd());
            Assert.True(p.IsModified(0, 2)); Assert.True(p.IsModified(7, 15)); Assert.False(p.IsModified(0, 3));

            var after = Directory.GetFiles(legacy).Select(f => (f, File.ReadAllText(f))).OrderBy(x => x.f).ToList();
            Assert.Equal(before, after);                                                       // the old folder is untouched
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class RecentProjectsTests
{
    [Fact]
    public void AddPutsFirstDeduplicatesAndCapsAtTen()
    {
        var list = new List<string>();
        for (int i = 0; i < 14; i++) list = RecentProjects.Add(list, $"/tmp/p{i}.sambient");
        Assert.Equal(10, list.Count);
        Assert.Equal(Path.GetFullPath("/tmp/p13.sambient"), list[0]);
        Assert.Equal(Path.GetFullPath("/tmp/p4.sambient"), list[9]);
        list = RecentProjects.Add(list, "/tmp/p8.sambient");           // already present: moves to the front, no duplicate
        Assert.Equal(10, list.Count);
        Assert.Equal(Path.GetFullPath("/tmp/p8.sambient"), list[0]);
        Assert.Single(list, e => e.EndsWith("p8.sambient"));
        list = RecentProjects.Add(list, "/TMP/P8.sambient");           // the file system is case-insensitive on macOS
        Assert.Single(list, e => e.EndsWith("p8.sambient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PruneDropsMissingFilesAndRemoveDropsOne()
    {
        var dir = ProjectTestUtil.TempDir();
        try
        {
            string a = Path.Combine(dir, "a.sambient"), b = Path.Combine(dir, "b.sambient"), gone = Path.Combine(dir, "gone.sambient");
            File.WriteAllText(a, "x"); File.WriteAllText(b, "x");
            var pruned = RecentProjects.Prune([a, gone, b, a, ""]);
            Assert.Equal([a, b], pruned);
            Assert.Equal([b], RecentProjects.Remove(pruned, a));
            Assert.Equal([a], RecentProjects.Prune([a, b], f => f == a));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class AutosaveTests
{
    [Fact]
    public void RecoverabilityRule()
    {
        var t = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(AutosaveStore.IsRecoverable(new AutosaveInfo(null, "Untitled", t), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));   // never saved
        Assert.True(AutosaveStore.IsRecoverable(new AutosaveInfo("/x.sambient", "X", t), null));                                              // file vanished
        Assert.True(AutosaveStore.IsRecoverable(new AutosaveInfo("/x.sambient", "X", t), t.AddMinutes(-5)));                                  // newer than the project file
        Assert.False(AutosaveStore.IsRecoverable(new AutosaveInfo("/x.sambient", "X", t), t.AddMinutes(5)));                                  // project saved after the autosave
        Assert.False(AutosaveStore.IsRecoverable(new AutosaveInfo("/x.sambient", "X", t), t));                                                // same instant: nothing to recover
    }

    [Fact]
    public void WriteFindLoadAndClear()
    {
        var dir = ProjectTestUtil.TempDir();
        try
        {
            var store = new AutosaveStore(Path.Combine(dir, "data"));
            Assert.Null(store.ReadInfo());
            Assert.Null(store.FindRecoverable());

            var p = Project.CreateNew("Auto");
            string file = Path.Combine(dir, "auto.sambient");
            Assert.Null(ProjectFile.Save(p, file));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
            p.Rename("Auto edited");
            var live = p.GetPalette(4, 4); live.Name = "LIVE"; live.Tracks[0].Steps[0].Notes.Add(StepNoteData.Make(70));
            Assert.True(store.Write(p, (PatternBank.SlotIndex(4, 4), live)));

            var info = store.FindRecoverable();
            Assert.NotNull(info);
            Assert.Equal(Path.GetFullPath(file), info!.ProjectPath);
            Assert.Equal("Auto edited", info.ProjectName);
            var r = store.Load();
            Assert.True(r.Ok);
            Assert.Equal("Auto edited", r.Project!.Name);
            PatternJsonTests.AssertSame(live, r.Project.GetPalette(4, 4));                    // the live edit travelled with the autosave
            Assert.False(p.IsModified(4, 4));                                                 // while the project itself was not touched

            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));                      // the project file is newer: nothing to recover
            Assert.Null(store.FindRecoverable());

            store.Clear();
            Assert.Null(store.ReadInfo());
            Assert.False(File.Exists(store.ProjectFilePath));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class SongPlaybackTests
{
    private static PatternPalette Small(string name, int note)
    {
        var p = new PatternPalette(name);
        p.SetGlobal(GlobalParamId.Bpm, 120);
        p.SetGlobal(GlobalParamId.ReverbType, 0);
        for (int l = 0; l < 4; l++) { p.SetLayer(l, ParamId.SeqLength, 4); p.SetLayer(l, ParamId.StepNote, (float)StepNote.Quarter1_4); p.SetLayer(l, ParamId.Release, 20); }
        p.SetLayer(0, ParamId.Attack, 5);
        p.Tracks[0].Steps[0].Notes.Add(StepNoteData.Make(note, 100, 4));
        return p;
    }

    private static SeqRig Rig(SongChain song)
    {
        var r = new SeqRig(waitForWaves: true);
        var proj = r.Seq.Bank.Project;
        var pals = new[] { Small("AAAA", 60), Small("BBBB", 62), Small("CCCC", 64) };
        for (int i = 0; i < 3; i++) { r.WaitForWaves(pals[i]); proj.SetPalette(4, i, pals[i]); }
        proj.SetChain(0, song);
        return r;
    }

    /// <summary>What PLAY SONG does: the project's song chain into the sequencer, then play from an entry.</summary>
    private static void PlaySong(SeqRig r, int start = 0)
    {
        var c = r.Seq.Bank.Project.Song;
        r.Seq.SetChain(c.Entries.Select(e => (e.Bank, e.Slot)).ToList(), c.Loop, c.Repeats, c.Relaxation, c.FadeSeconds, c.FadeInSeconds);
        Assert.True(r.Seq.PlayChainFrom(start));
    }

    private static SongChain Three(bool loop = false, int repeats = 1)
        => new() { Loop = loop, Repeats = repeats, Entries = [new ChainSlot(4, 0), new ChainSlot(4, 1), new ChainSlot(4, 2)] };

    [Fact]
    public void SongPlaysTheProjectChainInOrderAndStops()
    {
        using var r = Rig(Three());
        PlaySong(r);
        r.RenderSeconds(7.0);
        Assert.Equal([64, 65, 66], r.Of(SeqTapKind.PaletteSwitch).Select(e => e.Note).ToList());   // slot indices of the three palettes
        Assert.Equal(TransportState.Stopped, r.Seq.State);                                          // 3 cycles x 2 s, then the song ended
        Assert.Equal([60, 62, 64], r.Of(SeqTapKind.NoteOn, 0).Select(e => e.Note).ToList());        // and each palette's own note sounded
    }

    [Fact]
    public void SongCanStartFromASelectedEntry()
    {
        using var r = Rig(Three());
        PlaySong(r, start: 1);
        r.RenderSeconds(5.0);
        Assert.Equal([65, 66], r.Of(SeqTapKind.PaletteSwitch).Select(e => e.Note).ToList());
        Assert.Equal([62, 64], r.Of(SeqTapKind.NoteOn, 0).Select(e => e.Note).ToList());
        Assert.Equal(2, r.Seq.Chain.Index);
    }

    [Fact]
    public void SongRepeatsEachPaletteAndLoops()
    {
        using var r = Rig(Three(loop: true, repeats: 2));
        PlaySong(r);
        r.RenderSeconds(2 * 2 * 3 * 2 + 1.0);                                                       // two full passes (+ a little)
        var notes = r.Of(SeqTapKind.NoteOn, 0).Select(e => e.Note).ToList();
        Assert.Equal([60, 60, 62, 62, 64, 64, 60, 60, 62, 62, 64, 64], notes.Take(12).ToList());
        Assert.True(r.Seq.IsPlaying);
        Assert.True(r.Seq.Chain.Active);
    }

    [Fact]
    public void OutOfRangeStartIsClamped()
    {
        using var r = Rig(Three());
        PlaySong(r, start: 99);
        r.RenderSeconds(1.0);
        Assert.Equal(2, r.Seq.Chain.Index);
    }
}
