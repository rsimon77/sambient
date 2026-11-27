using Sambient.Dsp.Effects;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.Tests;

public class PatternJsonTests
{
    internal static void AssertSame(PatternPalette a, PatternPalette b)
    {
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.PatternBars, b.PatternBars);
        for (int l = 0; l < 4; l++) Assert.Equal(a.Layers[l], b.Layers[l]);
        foreach (var g in PatternPalette.PatternGlobals) Assert.Equal(a.Globals[(int)g], b.Globals[(int)g]);
        for (int t = 0; t < 4; t++)
            for (int s = 0; s < 64; s++)
            {
                var x = a.Tracks[t].Steps[s]; var y = b.Tracks[t].Steps[s];
                Assert.Equal(x.Notes, y.Notes);
                Assert.Equal(x.Probability, y.Probability);
                Assert.Equal(x.Locks.Select(k => (k.Key, k.Value)), y.Locks.Select(k => (k.Key, k.Value)));
            }
    }

    [Fact]
    public void RoundTripKeepsAllParametersAndSequences()
    {
        var p = FactoryPresets.Get(0, 0)!;
        p.PatternBars = 12;
        p.Name = "RT-TEST";
        p.Tracks[0].Steps[5].Notes.AddRange([StepNoteData.Make(60, 77, 3), StepNoteData.Make(64, 100, 1)]);
        p.Tracks[0].Steps[5].Probability = 75;
        p.Tracks[0].Steps[5].Locks[ParamId.Harmonic] = 101.5f;
        p.Tracks[0].Steps[5].Locks[ParamId.Wave] = 12;
        p.Tracks[3].Steps[63].Notes.Add(StepNoteData.Make(0, 1, 1));
        p.Layers[2][(int)ParamId.Cutoff] = 33.25f;
        p.SetGlobal(GlobalParamId.Transpose, -7); p.SetGlobal(GlobalParamId.Temperament, (float)Temperament.Chakra);
        p.SetGlobal(GlobalParamId.FxRouting, 1); p.SetGlobal(GlobalParamId.PatternLevel, 90); p.SetGlobal(GlobalParamId.RndSeq, 1);
        var q = PatternJson.TryParse(PatternJson.Serialize(p));
        Assert.NotNull(q);
        AssertSame(p, q!);
        Assert.Equal(101.5f, q!.Tracks[0].Steps[5].Locks[ParamId.Harmonic]);
    }

    [Fact]
    public void EveryFactorySlotRoundTripsAndHasAValidName()
    {
        for (int b = 0; b < 4; b++)
            for (int s = 0; s < 16; s++)
            {
                var p = FactoryPresets.Get(b, s)!;
                Assert.InRange(p.Name.Length, 4, 8);
                AssertSame(p, PatternJson.TryParse(PatternJson.Serialize(p))!);
            }
        Assert.Null(FactoryPresets.Get(4, 0));
        Assert.True(FactoryPresets.DesignedNames.Length >= 8);
        Assert.Equal(FactoryPresets.DesignedNames.Length, FactoryPresets.DesignedNames.Distinct().Count());
    }

    [Fact]
    public void LoadingIsTolerantOfUnknownMissingAndOutOfRangeData()
    {
        const string json = """
        {
          "format": "sambient-pattern", "version": 99, "futureField": { "x": [1,2,3] },
          "name": "TOLERANT-NAME-TOO-LONG",
          "globals": { "Bpm": 9999, "NoSuchGlobal": 5, "ReverbType": "7" },
          "layers": [
            { "Harmonic": 1000, "Wave": "oops", "Cutoff": 55, "FutureParam": 3, "Octave": -9 },
            "not an object",
            { "Resonance": 12 }
          ],
          "tracks": [
            { "steps": [
               { "i": 2, "notes": [ { "n": 300, "v": 0, "t": 0 }, { "n": 61 }, { "v": 5 } ], "prob": 3, "locks": { "Cutoff": 20, "Mute": 1, "Bogus": 4 } },
               { "i": 999, "notes": [ { "n": 60 } ] },
               { "notes": [ { "n": 60 } ] },
               42
            ], "extra": true }
          ],
          "tail": null
        }
        """;
        var p = PatternJson.TryParse(json);
        Assert.NotNull(p);
        Assert.Equal("TOLERANT", p!.Name);
        Assert.Equal(250f, p.GetGlobal(GlobalParamId.Bpm));
        Assert.Equal(7f, p.GetGlobal(GlobalParamId.ReverbType));
        Assert.Equal(127f, p.GetLayer(0, ParamId.Harmonic));
        Assert.Equal(ParamTable.Get(ParamId.Wave).Default, p.GetLayer(0, ParamId.Wave));    // unparsable -> default
        Assert.Equal(55f, p.GetLayer(0, ParamId.Cutoff));
        Assert.Equal(-3f, p.GetLayer(0, ParamId.Octave));
        Assert.Equal(ParamTable.Get(ParamId.Cutoff).Default, p.GetLayer(1, ParamId.Cutoff));  // missing -> default
        Assert.Equal(12f, p.GetLayer(2, ParamId.Resonance));
        var st = p.Tracks[0].Steps[2];
        Assert.Equal(2, st.Notes.Count);
        Assert.Equal(new StepNoteData(127, 1, 1), st.Notes[0]);
        Assert.Equal(25, st.Probability);
        Assert.Equal([ParamId.Cutoff], st.Locks.Keys.ToArray());                // Mute is not lockable, Bogus unknown
        Assert.All(p.Tracks[1].Steps, s => Assert.True(s.IsEmpty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{ \"format\": \"something-else\" }")]
    [InlineData("{ \"name\": ")]
    public void GarbageYieldsNullInsteadOfThrowing(string text) => Assert.Null(PatternJson.TryParse(text));

    [Fact]
    public void EmptyObjectGivesAnInitPalette()
    {
        var p = PatternJson.TryParse("{}");
        Assert.NotNull(p);
        AssertSame(new PatternPalette(), p!);
    }

    [Fact]
    public void NamesAreNormalizedToFourToEightCharacters()
    {
        Assert.Equal("AB  ", PatternPalette.NormalizeName("ab".ToUpper()));
        Assert.Equal("ABCDEFGH", PatternPalette.NormalizeName("  ABCDEFGHIJ "));
        Assert.Equal("INIT", PatternPalette.NormalizeName(""));
        Assert.Equal("INIT", PatternPalette.NormalizeName(null));
    }
}

public class PatternBankTests
{
    [Fact]
    public void UserCopyWinsOverPresetAndCanBeReverted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sambient-bank-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bank = new PatternBank(dir);
            Assert.True(bank.IsFactory(0, 2));
            var factory = bank.Load(0, 2);
            var mine = factory.Clone(); mine.Name = "MINE"; mine.SetLayer(0, ParamId.Cutoff, 5);
            bank.Save(0, 2, mine);
            Assert.True(File.Exists(Path.Combine(dir, "B1-03.json")));
            Assert.False(bank.IsFactory(0, 2));
            Assert.Equal("MINE", bank.Load(0, 2).Name.TrimEnd());
            Assert.Equal("MINE", new PatternBank(dir).GetName(0, 2).TrimEnd());
            Assert.True(bank.RevertToFactory(0, 2));
            PatternJsonTests.AssertSame(factory, bank.Load(0, 2));
            Assert.False(bank.RevertToFactory(0, 2));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void UserBanksStartAsInitAndSaveToTheirOwnFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sambient-bank-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bank = new PatternBank(dir);
            Assert.Equal("INIT", bank.Load(4, 0).Name.TrimEnd());
            var p = bank.Load(7, 15); p.Name = "LAST"; p.Tracks[1].Steps[9].Notes.Add(StepNoteData.Make(55, 90, 2));
            bank.Save(7, 15, p);
            Assert.True(File.Exists(Path.Combine(dir, "B8-16.json")));
            PatternJsonTests.AssertSame(p, bank.Load(7, 15));
            Assert.Equal("B8-16", PatternBank.SlotId(7, 15));
            Assert.Throws<ArgumentOutOfRangeException>(() => bank.Load(8, 0));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void CorruptFileFallsBackToThePresetOrInit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sambient-bank-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "B1-01.json"), "{ broken");
            File.WriteAllText(Path.Combine(dir, "B5-01.json"), "garbage");
            var bank = new PatternBank(dir);
            Assert.Equal(FactoryPresets.DesignedNames[0], bank.Load(0, 0).Name);
            Assert.Equal("INIT", bank.Load(4, 0).Name.TrimEnd());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class PaletteSwitchingTests
{
    private static PatternPalette Small(string name, float harmonic, int note, float bpm = 120)
    {
        var p = new PatternPalette(name);
        p.SetGlobal(GlobalParamId.Bpm, bpm);
        p.SetGlobal(GlobalParamId.ReverbType, 0);
        for (int l = 0; l < 4; l++) { p.SetLayer(l, ParamId.SeqLength, 4); p.SetLayer(l, ParamId.StepNote, (float)StepNote.Quarter1_4); p.SetLayer(l, ParamId.Release, 20); }
        p.SetLayer(0, ParamId.Harmonic, harmonic);
        p.SetLayer(0, ParamId.Attack, 5);
        for (int s = 0; s < 4; s += 4) p.Tracks[0].Steps[s].Notes.Add(StepNoteData.Make(note, 100, 4));
        return p;
    }

    private static SeqRig Rig(params (int slot, PatternPalette p)[] pals)
    {
        var r = new SeqRig(waitForWaves: true);
        foreach (var (slot, p) in pals) r.Seq.Bank.Save(slot / 16, slot % 16, p);
        return r;
    }

    [Fact]
    public void SelectWhileStoppedLoadsImmediately()
    {
        using var r = Rig((64, Small("ALPH", 11, 60, 100)), (65, Small("BETA", 99, 62, 80)));
        r.Seq.SelectPalette(4, 0); r.Render(64);
        Assert.Equal(11f, r.Engine.Parameters.GetBase(0, ParamId.Harmonic));
        Assert.Equal(100f, r.Engine.Parameters.Get(GlobalParamId.Bpm));
        Assert.Equal("ALPH", r.Seq.CurrentName.TrimEnd());
        Assert.Equal((4, 0), (r.Seq.CurrentBank, r.Seq.CurrentSlot));
        r.Seq.SelectPalette(4, 1); r.Render(64);
        Assert.Equal(99f, r.Engine.Parameters.GetBase(0, ParamId.Harmonic));
        Assert.Equal("BETA", r.Seq.CurrentName.TrimEnd());
        Assert.Equal(80f, r.Engine.Parameters.Get(GlobalParamId.Bpm));
    }

    [Fact]
    public void SelectWhilePlayingIsQueuedUntilTheCycleCompletes()
    {
        using var r = Rig((64, Small("ALPH", 11, 60)), (65, Small("BETA", 99, 62)));
        int changed = 0;
        r.Seq.PaletteChanged += () => changed++;
        r.Seq.SelectPalette(4, 0); r.Render(64);
        r.Events.Clear();
        long t0 = r.Rendered;
        r.Seq.Play();
        r.RenderSeconds(0.7);
        r.Seq.SelectPalette(4, 1);
        Assert.Equal((4, 1), r.Seq.QueuedSlot);
        r.RenderSeconds(1.0);                                         // t = 1.7: still the old palette
        Assert.Equal(11f, r.Engine.Parameters.GetBase(0, ParamId.Harmonic));
        Assert.Equal("ALPH", r.Seq.CurrentName.TrimEnd());
        r.RenderSeconds(0.5);                                         // t = 2.2: cycle (4 x 0.5 s) completed at 2.0
        Assert.Equal(99f, r.Engine.Parameters.GetBase(0, ParamId.Harmonic));
        var sw = r.Of(SeqTapKind.PaletteSwitch);
        Assert.Single(sw);
        Assert.True(Math.Abs(sw[0].Sample - (t0 + 2.0 * SeqRig.Fs)) < 2);
        Assert.Equal(65, sw[0].Note);
        Assert.Equal("BETA", r.Seq.CurrentName.TrimEnd());
        Assert.Null(r.Seq.QueuedSlot);
        Assert.True(changed >= 1);
        // the new palette's sequence plays from step 1 with its own note
        Assert.Equal(62, r.Of(SeqTapKind.NoteOn, 0).Last().Note);
    }

    [Fact]
    public void BpmModeGlobalKeepsTheCurrentTempo()
    {
        using var r = Rig((64, Small("ALPH", 11, 60, 70)));
        r.Engine.SetGlobal(GlobalParamId.Bpm, 133);
        r.Seq.BpmMode = BpmMode.Global;
        r.Seq.SelectPalette(4, 0); r.Render(64);
        Assert.Equal(133f, r.Engine.Parameters.Get(GlobalParamId.Bpm));
        r.Seq.BpmMode = BpmMode.Pattern;
        r.Seq.SelectPalette(4, 0); r.Render(64);
        Assert.Equal(70f, r.Engine.Parameters.Get(GlobalParamId.Bpm));
    }

    [Fact]
    public void ReloadRestoresTheSavedStateSaveAsPersistsAndInitClears()
    {
        using var r = Rig((64, Small("ALPH", 11, 60)));
        r.Seq.SelectPalette(4, 0); r.Render(64);
        r.Engine.SetParam(0, ParamId.Harmonic, 77);
        r.Seq.AddStepNote(1, 3, 70);
        Assert.Equal("ALPH", r.Seq.CurrentName.TrimEnd());
        r.Seq.Reload(); r.Render(64);
        Assert.Equal(11f, r.Engine.Parameters.GetBase(0, ParamId.Harmonic));
        Assert.False(r.Seq.GetStep(1, 3).HasNotes);
        // edit + Save
        r.Engine.SetParam(0, ParamId.Harmonic, 55);
        r.Seq.AddStepNote(1, 3, 70);
        Assert.Equal("NEWN", r.Seq.Rename("newn".ToUpper()));
        Assert.True(r.Seq.Save());
        var saved = r.Seq.Bank.Load(4, 0);
        Assert.Equal(55f, saved.GetLayer(0, ParamId.Harmonic));
        Assert.Equal(70, saved.Tracks[1].Steps[3].Notes[0].Note);
        Assert.Equal("NEWN", saved.Name);
        // Save as elsewhere
        r.Seq.SaveAs(5, 7);
        Assert.Equal((5, 7), (r.Seq.CurrentBank, r.Seq.CurrentSlot));
        Assert.True(File.Exists(Path.Combine(r.Dir, "B6-08.json")));
        // Init
        r.Seq.Init(); r.Render(64);
        Assert.Equal(ParamTable.Get(ParamId.Harmonic).Default, r.Engine.Parameters.GetBase(0, ParamId.Harmonic));
        Assert.False(r.Seq.GetStep(1, 3).HasNotes);
        Assert.Equal(-1, r.Seq.CurrentSlot);
        Assert.False(r.Seq.Save());
    }

    [Fact]
    public void CopyLayerWorksBetweenSynthLayersAndNeverWithTheNoiseLayer()
    {
        using var r = new SeqRig();
        r.Engine.SetParam(0, ParamId.Cutoff, 44); r.Engine.SetParam(0, ParamId.Wave, 5);
        r.Seq.AddStepNote(0, 2, 61); r.Seq.SetLock(0, 2, ParamId.Harmonic, 9);
        Assert.True(r.Seq.CopyLayer(0, 2));
        Assert.Equal(44f, r.Engine.Parameters.GetBase(2, ParamId.Cutoff));
        Assert.Equal(5f, r.Engine.Parameters.GetBase(2, ParamId.Wave));
        Assert.Equal(61, r.Seq.GetStep(2, 2).Notes[0].Note);
        Assert.Equal(9f, r.Seq.GetStep(2, 2).Locks[0].Value);
        Assert.False(r.Seq.CopyLayer(0, 3));
        Assert.False(r.Seq.CopyLayer(3, 1));
        Assert.True(r.Seq.CopyLayer(3, 3));
        // from another palette
        var other = new PatternPalette("OTHR"); other.SetLayer(1, ParamId.Resonance, 99); other.Tracks[1].Steps[0].Notes.Add(StepNoteData.Make(48));
        r.Seq.Bank.Save(6, 0, other);
        Assert.True(r.Seq.CopyLayerFrom(6, 0, 1, 0));
        Assert.Equal(99f, r.Engine.Parameters.GetBase(0, ParamId.Resonance));
        Assert.Equal(48, r.Seq.GetStep(0, 0).Notes[0].Note);
        Assert.False(r.Seq.CopyLayerFrom(6, 0, 3, 1));
    }

    [Fact]
    public void ChainPlaysInOrderThenStopsOrLoops()
    {
        List<int> Order(bool loop, double seconds, out TransportState state)
        {
            using var r = Rig((64, Small("AAAA", 10, 60)), (65, Small("BBBB", 20, 62)), (66, Small("CCCC", 30, 64)));
            r.Seq.SetChain([(4, 0), (4, 1), (4, 2)], loop: loop);
            Assert.True(r.Seq.PlayChain());
            r.RenderSeconds(seconds);
            state = r.Seq.State;
            return r.Of(SeqTapKind.PaletteSwitch).Select(e => e.Note).ToList();
        }
        var o = Order(false, 7.0, out var st);
        Assert.Equal([64, 65, 66], o);
        Assert.Equal(TransportState.Stopped, st);                     // 3 cycles x 2 s = 6 s, then the chain ended
        o = Order(true, 7.0, out st);
        Assert.Equal([64, 65, 66, 64], o);
        Assert.Equal(TransportState.Playing, st);
    }

    [Fact]
    public void ChainRepeatsEachPaletteTheRequestedNumberOfTimes()
    {
        using var r = Rig((64, Small("AAAA", 10, 60)), (65, Small("BBBB", 20, 62)));
        r.Seq.SetChain([(4, 0), (4, 1)], loop: false, repeats: 3);
        r.Seq.PlayChain();
        r.RenderSeconds(7);
        var sw = r.Of(SeqTapKind.PaletteSwitch);
        Assert.Equal(2, sw.Count);
        Assert.True(Math.Abs(sw[1].Sample - 3 * 2.0 * SeqRig.Fs) < 2);
        Assert.Equal(2, r.Seq.Chain.Index + 1);
    }

    [Fact]
    public void RelaxationRepeatsFadesOutSwitchesAndFadesBackIn()
    {
        using var r = Rig((64, Small("AAAA", 40, 60)), (65, Small("BBBB", 40, 67)));
        for (int s = 0; s < 2; s++) { }
        r.Seq.SetChain([(4, 0), (4, 1)], loop: true, repeats: 2, relaxation: true, fadeSeconds: 5);
        r.Seq.PlayChain();
        r.RenderSeconds(3);
        r.RenderSeconds(1);  double full = r.LastRms;                 // [3,4] s: full level
        Assert.True(full > 1e-3, $"full {full}");
        r.RenderSeconds(4);
        r.RenderSeconds(0.5); double late = r.LastRms;                // [8,8.5] s: close to the end of the fade-out
        Assert.True(late < full * 0.25, $"late {late} full {full}");
        Assert.True(r.Seq.Chain.FadePhase == 1 && r.Seq.Chain.FadeGain < 0.4f);
        r.RenderSeconds(0.5);
        r.RenderSeconds(0.5);                                         // [9,9.5]: new palette, fading in
        double early = r.LastRms;
        Assert.True(early < full * 0.3, $"early {early}");
        r.RenderSeconds(4.5); r.RenderSeconds(0.5);                   // [14,14.5]
        Assert.True(r.LastRms > full * 0.5, $"back {r.LastRms}");
        double sr = SeqRig.Fs;
        var fs = r.Of(SeqTapKind.FadeOutStart); var fe = r.Of(SeqTapKind.FadeOutEnd); var sw = r.Of(SeqTapKind.PaletteSwitch);
        Assert.True(Math.Abs(fs[0].Sample - 4 * sr) < 2, $"{fs[0].Sample}");       // 2 cycles x 2 s
        Assert.True(Math.Abs(fe[0].Sample - 9 * sr) < 2, $"{fe[0].Sample}");       // + 5 s fade
        Assert.Equal([64, 65], sw.Select(e => e.Note).ToArray());
        Assert.True(Math.Abs(sw[1].Sample - 9 * sr) < 2);
        Assert.Equal(1, r.Seq.Chain.Index);
    }

    [Fact]
    public void RelaxationWithoutLoopStopsAfterTheLastFade()
    {
        using var r = Rig((64, Small("AAAA", 40, 60)));
        r.Seq.SetChain([(4, 0)], loop: false, repeats: 2, relaxation: true, fadeSeconds: 5);
        r.Seq.PlayChain();
        r.RenderSeconds(9.5);
        Assert.Equal(TransportState.Stopped, r.Seq.State);
        r.RenderSeconds(3);
        Assert.Equal(1f, r.Seq.Chain.FadeGain, 3);                     // the master level has come back (2 s soft return)
    }

    [Fact]
    public void ManualQueuedSelectBeatsTheChainAtTheNextCycleEnd()
    {
        using var r = Rig((64, Small("AAAA", 40, 60)), (65, Small("BBBB", 41, 62)), (66, Small("CCCC", 42, 64)));
        r.Seq.SetChain([(4, 0), (4, 1)], loop: true, repeats: 4);
        r.Seq.PlayChain();
        r.RenderSeconds(0.5);
        r.Seq.SelectPalette(4, 2);
        r.RenderSeconds(2.0);
        Assert.Equal([64, 66], r.Of(SeqTapKind.PaletteSwitch).Select(e => e.Note).ToArray());
    }

    [Fact]
    public void AllFactoryPresetsLoadAndRenderFiniteAudio()
    {
        using var r = new SeqRig(waitForWaves: false);
        for (int s = 0; s < 12; s++)
        {
            r.WaitForWaves(r.Seq.Bank.Load(0, s));
            r.Seq.LoadPaletteNow(0, s);
            r.Engine.SetGlobal(GlobalParamId.ReverbType, (float)(s % 2 == 0 ? ReverbType.LargeM : ReverbType.Off));
            r.Seq.Play();
            r.RenderSeconds(3);
            Assert.True(r.LastRms > 1e-5, $"preset {s} silent");
            Assert.True(r.Engine.PeakLeft <= 1.0001f && float.IsFinite(r.Engine.PeakLeft));
            r.Seq.Stop(); r.RenderSeconds(0.4);
        }
    }

    [Fact]
    public void RenderDoesNotAllocateWhileTheSequencerPlaysPLocksAndAChainSwitches()
    {
        using var r = Rig((64, Small("AAAA", 40, 60)), (65, Small("BBBB", 41, 62)));
        var a = r.Seq.Bank.Load(4, 0); var b = r.Seq.Bank.Load(4, 1);
        foreach (var p in new[] { a, b })
        {
            for (int t = 0; t < 4; t++)
            {
                p.SetLayer(t, ParamId.SeqLength, 8); p.SetLayer(t, ParamId.StepNote, (float)StepNote.Sixteenth1_16);
                for (int s = 0; s < 8; s++)
                {
                    p.Tracks[t].Steps[s].Notes.Add(StepNoteData.Make(48 + t * 7 + s, 90, 1 + s % 3));
                    p.Tracks[t].Steps[s].Notes.Add(StepNoteData.Make(55 + s, 70));
                    p.Tracks[t].Steps[s].Locks[ParamId.Harmonic] = 10 + s * 12;
                    p.Tracks[t].Steps[s].Locks[ParamId.Cutoff] = 40 + s * 8;
                    if (s % 2 == 1) p.Tracks[t].Steps[s].Probability = 60;
                }
            }
            p.SetGlobal(GlobalParamId.RndSeq, 1);
        }
        r.Seq.Bank.Save(4, 0, a); r.Seq.Bank.Save(4, 1, b);
        r.Seq.SetChain([(4, 0), (4, 1)], loop: true, repeats: 2);
        r.Seq.MetronomeOn = true; r.Seq.MetronomeVolume = 8;
        r.Seq.PlayChain();
        var buf = new float[512];
        r.Seq.Tap = null;                                          // the rig's tap collects into a List (allocates)
        for (int i = 0; i < 1000; i++) r.Engine.Render(buf, 256);       // warm-up incl. chain switches (first-use JIT)
        long allocated = 0;
        for (int i = 0; i < 1500; i++)
        {
            if (i == 300) r.Seq.SelectPalette(4, 1);            // queued switch (prepared on this thread, outside the audio path)
            long before = GC.GetAllocatedBytesForCurrentThread();
            r.Engine.Render(buf, 256);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, allocated);
    }
}
