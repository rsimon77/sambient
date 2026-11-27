using Sambient.App.ViewModels;
using Sambient.Engine;
using Sambient.Seq;
using Xunit;

namespace Sambient.App.Tests;

public class StepGridTests
{
    private static TrackSnapshot Track(int length, Action<StepData[]>? edit = null)
    {
        var steps = Enumerable.Range(0, SeqLimits.MaxSteps).Select(_ => new StepData()).ToArray();
        edit?.Invoke(steps);
        var snaps = steps.Select(s => new StepSnapshot(s.Notes.ToArray(), s.Probability, s.Locks.Select(k => new ParamLock(k.Key, k.Value)).ToArray())).ToArray();
        return new TrackSnapshot(0, length, StepNote.Sixteenth1_16, 0, snaps);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 2)]
    [InlineData(33, 3)]
    [InlineData(64, 4)]
    public void PagesAreEnabledByLength(int length, int enabledPages)
    {
        for (int p = 0; p < StepGrid.PageCount; p++)
            Assert.Equal(p < enabledPages, StepGrid.IsPageEnabled(p, length));
        Assert.Equal(enabledPages - 1, StepGrid.LastPage(length));
    }

    [Fact]
    public void PageLabels() => Assert.Equal(["1–16", "17–32", "33–48", "49–64"], Enumerable.Range(0, 4).Select(StepGrid.PageLabel));

    [Fact]
    public void Cells_ShowNotesLocksProbability()
    {
        var t = Track(16, s =>
        {
            s[0].Notes.Add(StepNoteData.Make(60)); s[0].Notes.Add(StepNoteData.Make(64));
            s[2].Locks[ParamId.Cutoff] = 90; s[2].Probability = 50;
        });
        var c = StepGrid.Derive(t, 0);
        Assert.Equal(16, c.Length);
        Assert.True(c[0].HasNotes); Assert.Equal(2, c[0].NoteCount); Assert.False(c[0].HasLocks); Assert.Equal("", c[0].ProbabilityText);
        Assert.False(c[1].HasNotes);
        Assert.True(c[2].HasLocks); Assert.Equal("50%", c[2].ProbabilityText); Assert.False(c[2].HasNotes);
        Assert.All(c, x => Assert.True(x.InLength));
    }

    [Fact]
    public void Cells_BeyondLengthAreNotInLength()
    {
        var c = StepGrid.Derive(Track(10), 0);
        Assert.True(c[9].InLength);
        Assert.False(c[10].InLength);
        var c2 = StepGrid.Derive(Track(10), 1);
        Assert.All(c2, x => Assert.False(x.InLength));
        Assert.Equal(16, c2[0].Index);
    }

    [Fact]
    public void Tie_DrawsStartMidEnd()
    {
        var t = Track(16, s => s[2].Notes.Add(StepNoteData.Make(60, 100, 4)));   // steps 2..5
        var c = StepGrid.Derive(t, 0);
        Assert.Equal(TiePart.None, c[1].Tie);
        Assert.Equal(TiePart.Start, c[2].Tie);
        Assert.Equal(TiePart.Mid, c[3].Tie);
        Assert.Equal(TiePart.Mid, c[4].Tie);
        Assert.Equal(TiePart.End, c[5].Tie);
        Assert.Equal(TiePart.None, c[6].Tie);
        Assert.True(c[2].TieToNext && !c[2].TieFromPrevious);
        Assert.True(c[3].TieToNext && c[3].TieFromPrevious);
        Assert.True(!c[5].TieToNext && c[5].TieFromPrevious);
    }

    [Fact]
    public void Tie_CrossesPagesAndIsClampedToLength()
    {
        var t = Track(20, s => s[14].Notes.Add(StepNoteData.Make(60, 100, 40)));
        var p0 = StepGrid.Derive(t, 0);
        var p1 = StepGrid.Derive(t, 1);
        Assert.Equal(TiePart.Start, p0[14].Tie);
        Assert.Equal(TiePart.Mid, p0[15].Tie);
        Assert.Equal(TiePart.Mid, p1[0].Tie);
        Assert.Equal(TiePart.End, p1[3].Tie);               // last step of a 20-step track = index 19
        Assert.Equal(TiePart.None, p1[4].Tie);
    }

    [Fact]
    public void Tie_BackToBackNotesChain()
    {
        var t = Track(16, s => { s[0].Notes.Add(StepNoteData.Make(60, 100, 2)); s[1].Notes.Add(StepNoteData.Make(62, 100, 2)); });
        var c = StepGrid.Derive(t, 0);
        Assert.Equal(TiePart.Start, c[0].Tie);
        Assert.Equal(TiePart.Mid, c[1].Tie);                // covered by the first tie and starting the second
        Assert.Equal(TiePart.End, c[2].Tie);
    }
}

public class NamingTests
{
    [Theory]
    [InlineData("deepdrft", "DEEPDRFT")]
    [InlineData("a b!c", "ABC")]
    [InlineData("tooooolongname", "TOOOOOLO")]
    [InlineData("x.y-9", "X.Y-9")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void PatternNames_AreSanitized(string? input, string expected) => Assert.Equal(expected, PatternNames.Sanitize(input));

    [Theory]
    [InlineData(60, "C4")]
    [InlineData(61, "C#4")]
    [InlineData(69, "A4")]
    [InlineData(0, "C-1")]
    [InlineData(127, "G9")]
    public void NoteNames(int note, string name) => Assert.Equal(name, Sambient.App.ViewModels.NoteNames.Name(note));

    [Fact]
    public void ParamLabels() { Assert.Equal("CUTOFF", Sambient.App.ViewModels.ParamLabels.Of(ParamId.Cutoff)); Assert.Equal("MOD RATE", Sambient.App.ViewModels.ParamLabels.Of(ParamId.ModRate)); }
}

public class TuningLabelTests
{
    [Fact]
    public void KeyLabelCount_MatchesEngineKeyCount()
    {
        foreach (var t in Enum.GetValues<Temperament>())
            Assert.Equal(Tuning.KeyCount(t), TuningLabels.KeyLabels(t).Count);
        Assert.Equal(8, TuningLabels.TemperamentNames.Length);
    }

    [Fact]
    public void KeyLabels_PerTemperament()
    {
        Assert.Equal("C", TuningLabels.KeyLabels(Temperament.PureMajor)[0]);
        Assert.Equal("B", TuningLabels.KeyLabels(Temperament.PureMinor)[11]);
        Assert.Equal("Db", TuningLabels.KeyLabels(Temperament.PythagoreanFlat)[1]);
        Assert.Equal("C#", TuningLabels.KeyLabels(Temperament.PythagoreanSharp)[1]);
        Assert.Equal(["174", "285", "396", "417", "528", "639", "741", "852", "963"], TuningLabels.KeyLabels(Temperament.Solfeggio));
        Assert.Equal(["1st", "2nd", "3rd", "4th", "5th", "6th", "7th"], TuningLabels.KeyLabels(Temperament.Chakra));
        Assert.Equal(["1st", "2nd", "3rd", "4th", "5th", "6th", "7th"], TuningLabels.KeyLabels(Temperament.Planetary));
        Assert.False(TuningLabels.KeyEnabled(Temperament.Equal));
        Assert.True(TuningLabels.KeyEnabled(Temperament.Solfeggio));
    }

    [Theory]
    [InlineData(0, 440.0)]
    [InlineData(1200, 880.0)]
    [InlineData(-1200, 220.0)]
    [InlineData(75, 459.5)]
    public void CentsToHz(double cents, double hz) => Assert.Equal(hz, TuningLabels.CentsToHz(cents), 1);

    [Fact]
    public void HzToCents_RoundTrips()
    {
        foreach (double c in new[] { -75.0, -12.5, 0, 33, 75 })
            Assert.Equal(c, TuningLabels.HzToCents(TuningLabels.CentsToHz(c)), 6);
    }

    [Fact]
    public void Formatting()
    {
        Assert.Equal("+0 ct", TuningLabels.FormatCents(0));
        Assert.Equal("+12 ct", TuningLabels.FormatCents(12.4));
        Assert.Equal("-1200 ct", TuningLabels.FormatCents(-1200));
        Assert.Equal("440.0 Hz", TuningLabels.FormatMaster(0, true));
        Assert.Equal("+0 ct", TuningLabels.FormatMaster(0, false));
    }
}

public class SequencerWiringTests
{
    [Fact]
    public void LoadModel_CarriesPaletteOverToANewSequencer_AndAppliesItOnTheAudioThread()
    {
        var p1 = new ParameterStore();
        var e1 = new AudioEngine(48000, p1);
        var bank = new PatternBank();
        var s1 = new Sequencer(e1, bank);
        s1.SelectPalette(0, 2);
        var buf = new float[512];
        e1.Render(buf, 256);
        s1.Poll();
        Assert.True(s1.PaletteApplied);
        s1.SetStepNote(0, 3, 60);
        var model = s1.Capture();
        int bankIdx = s1.CurrentBank, slot = s1.CurrentSlot;

        var e2 = new AudioEngine(44100, p1);
        var s2 = new Sequencer(e2, bank);
        s2.LoadModel(model, PatternBank.SlotIndex(bankIdx, slot));
        Assert.Equal(bankIdx, s2.CurrentBank);
        Assert.Equal(slot, s2.CurrentSlot);
        Assert.False(s2.PaletteApplied);
        e2.Render(buf, 256);
        Assert.True(s2.PaletteApplied);
        Assert.True(s2.GetStep(0, 3).HasNotes);
    }

    [Fact]
    public void BaseValuesIgnoreLocks_WhileSetLayerParamWritesBase()
    {
        var p = new ParameterStore();
        var e = new AudioEngine(48000, p);
        var s = new Sequencer(e, new PatternBank());
        s.SetLayerParam(0, ParamId.Cutoff, 55);
        Assert.Equal(55, p.GetBase(0, ParamId.Cutoff));
        s.SetLock(0, 0, ParamId.Cutoff, 99);
        Assert.Equal(55, p.GetBase(0, ParamId.Cutoff));       // a lock never changes the base value shown on the knob
    }
}
