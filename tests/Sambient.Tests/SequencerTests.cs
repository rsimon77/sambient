using Sambient.Dsp.Effects;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.Tests;

/// <summary>Engine + sequencer on an offline clock; collects the sequencer's audio-thread tap events.</summary>
internal sealed class SeqRig : IDisposable
{
    public const int Fs = 48000;
    public readonly AudioEngine Engine;
    public readonly Sequencer Seq;
    public readonly List<SeqTapEvent> Events = new();
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "sambient-tests-" + Guid.NewGuid().ToString("N"));
    private readonly float[] _buf = new float[AudioEngine.MaxBlock * 2];
    public long Rendered;
    public double LastRms;

    public SeqRig(bool waitForWaves = false)
    {
        Engine = new AudioEngine(Fs);
        if (waitForWaves) Task.WaitAll(Engine.Bank.EnsureBuiltAsync(1), Engine.Bank.EnsureBuiltAsync(3), Engine.Bank.EnsureBuiltAsync(8), Engine.Noise.EnsureBuiltAsync(1));
        Seq = new Sequencer(Engine, new PatternBank(Dir));
        Seq.Tap = e => Events.Add(e);
        Engine.SetGlobal(GlobalParamId.Bpm, 120);
        Engine.SetGlobal(GlobalParamId.ReverbType, 0);        // cheaper offline renders
        for (int l = 0; l < 4; l++) Engine.SetParam(l, ParamId.Release, 20);
    }

    public void Render(int frames, int block = 256)
    {
        double sum = 0; long n = 0;
        while (frames > 0)
        {
            int b = Math.Min(block, frames);
            Engine.Render(_buf, b);
            for (int i = 0; i < b * 2; i++) sum += (double)_buf[i] * _buf[i];
            n += b * 2;
            Rendered += b; frames -= b;
            Seq.Poll();
        }
        LastRms = Math.Sqrt(sum / Math.Max(1, n));
    }

    /// <summary>Waits until every wavetable / noise slot a palette (and its p-locks) uses is built, so offline renders are not racing the builder threads.</summary>
    public void WaitForWaves(PatternPalette p)
    {
        var tasks = new List<Task>();
        for (int l = 0; l < 4; l++)
        {
            var waves = new HashSet<int> { (int)p.GetLayer(l, ParamId.Wave) };
            foreach (var st in p.Tracks[l].Steps) if (st.Locks.TryGetValue(ParamId.Wave, out var w)) waves.Add((int)w);
            foreach (var w in waves) tasks.Add(l == 3 ? Engine.Noise.EnsureBuiltAsync(w) : Engine.Bank.EnsureBuiltAsync(w));
        }
        Task.WaitAll(tasks.ToArray());
    }

    public void RenderSeconds(double s, int block = 256) => Render((int)(s * Fs), block);

    public List<SeqTapEvent> Of(SeqTapKind k, int track = -1) => Events.Where(e => e.Kind == k && (track < 0 || e.Track == track)).ToList();

    public static double StepSamples(double quarters, double bpm) => quarters * 60.0 / bpm * Fs;

    public void Dispose() { try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch (IOException) { } }
}

public class SequencerTimingTests
{
    public static IEnumerable<object[]> Timings() => new[]
    {
        new object[] { 120.0, StepNote.Sixteenth1_16, 0.25 },
        new object[] { 90.0, StepNote.DottedEighth, 0.75 },
        new object[] { 100.0, StepNote.HalfTriplet, 4.0 / 3 },
        new object[] { 77.0, StepNote.QuarterTriplet, 2.0 / 3 },
        new object[] { 250.0, StepNote.ThirtySecond1_32, 0.125 },
        new object[] { 40.0, StepNote.DottedSixteenth, 0.375 },
        new object[] { 133.3, StepNote.Eighth1_8, 0.5 },
    };

    [Theory]
    [MemberData(nameof(Timings))]
    public void StepsStartOnTheExactSample(double bpm, StepNote sn, double quarters)
    {
        using var r = new SeqRig();
        r.Engine.SetGlobal(GlobalParamId.Bpm, (float)bpm);
        r.Seq.SetTrackLength(0, 5);
        r.Seq.SetTrackStepNote(0, sn);
        for (int s = 0; s < 5; s++) r.Seq.AddStepNote(0, s, 60 + s);
        r.Seq.Play();
        double step = SeqRig.StepSamples(quarters, r.Engine.Parameters.Get(GlobalParamId.Bpm));
        r.Render((int)(step * 13) + 10, block: 333);       // odd block size: events fall inside blocks
        var ons = r.Of(SeqTapKind.NoteOn, 0);
        Assert.True(ons.Count >= 13, $"{ons.Count} note-ons");
        for (int k = 0; k < 13; k++)
        {
            double expected = k * step;
            Assert.True(Math.Abs(ons[k].Sample - expected) < 1.0, $"step {k}: {ons[k].Sample} vs {expected}");
            Assert.Equal(60 + k % 5, ons[k].Note);
        }
    }

    [Fact]
    public void TracksRunPolymetricallyWithTheirOwnStepLengths()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 3); r.Seq.SetTrackStepNote(0, StepNote.Sixteenth1_16);      // 3 x 1/16
        r.Seq.SetTrackLength(1, 4); r.Seq.SetTrackStepNote(1, StepNote.QuarterTriplet);     // 4 x 1/4T
        for (int s = 0; s < 3; s++) r.Seq.AddStepNote(0, s, 60);
        for (int s = 0; s < 4; s++) r.Seq.AddStepNote(1, s, 64);
        r.Seq.Play();
        r.RenderSeconds(6);
        double s0 = SeqRig.StepSamples(0.25, 120), s1 = SeqRig.StepSamples(2.0 / 3, 120);
        var a = r.Of(SeqTapKind.Step, 0); var b = r.Of(SeqTapKind.Step, 1);
        for (int k = 0; k < a.Count; k++) { Assert.True(Math.Abs(a[k].Sample - k * s0) < 1.0); Assert.Equal(k % 3, a[k].Step); }
        for (int k = 0; k < b.Count; k++) { Assert.True(Math.Abs(b[k].Sample - k * s1) < 1.0); Assert.Equal(k % 4, b[k].Step); }
        Assert.True(a.Count > 20 && b.Count > 5);
    }

    [Fact]
    public void BlockSizeDoesNotChangeEventTimes()
    {
        List<long> Run(int block)
        {
            using var r = new SeqRig();
            r.Seq.SetTrackLength(0, 4); r.Seq.SetTrackStepNote(0, StepNote.DottedEighth);
            for (int s = 0; s < 4; s++) r.Seq.AddStepNote(0, s, 60 + s);
            r.Seq.Play();
            r.RenderSeconds(5, block);
            return r.Of(SeqTapKind.NoteOn).Select(e => e.Sample).ToList();
        }
        var a = Run(64); var b = Run(1000); var c = Run(4096);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a, b); Assert.Equal(a, c);
    }

    [Fact]
    public void BpmChangeTakesEffectAtTheNextStep()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 8); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        for (int s = 0; s < 8; s++) r.Seq.AddStepNote(0, s, 60);
        r.Seq.Play();
        r.Render((int)(SeqRig.StepSamples(1, 120) * 2.5));
        r.Engine.SetGlobal(GlobalParamId.Bpm, 240);
        r.RenderSeconds(2);
        var st = r.Of(SeqTapKind.Step, 0);
        double d01 = st[1].Sample - st[0].Sample, d_last = st[^1].Sample - st[^2].Sample;
        Assert.True(Math.Abs(d01 - 24000) < 1.5);
        Assert.True(Math.Abs(d_last - 12000) < 1.5);
    }

    [Fact]
    public void TiesHoldWithoutRetriggerAndEndInTheLastStep()
    {
        using var r = new SeqRig(waitForWaves: true);
        r.Seq.SetTrackLength(0, 8); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        r.Seq.SetTie(0, 0, 2, 60);                       // steps 0..2
        r.Seq.AddStepNote(0, 4, 67);
        r.Seq.Play();
        r.RenderSeconds(1.2);
        Assert.Single(r.Of(SeqTapKind.NoteOn, 0), e => e.Note == 60 && e.Sample < 60000);
        Assert.True(r.Engine.ActiveVoices >= 1);
        r.RenderSeconds(1.0);
        var offs = r.Of(SeqTapKind.NoteOff, 0).Where(e => e.Note == 60).ToList();
        double step = SeqRig.StepSamples(1, 120);
        Assert.True(Math.Abs(offs[0].Sample - (2 + 0.98) * step) < 1.5, $"{offs[0].Sample}");
        Assert.Equal(3, r.Seq.GetStep(0, 0).Notes[0].Tie);
    }

    [Fact]
    public void TieCannotWrapPastTheEndOfTheTrack()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 4);
        Assert.False(r.Seq.SetTie(0, 3, 1, 60));
        Assert.True(r.Seq.SetTie(0, 2, 20, 60));
        Assert.Equal(2, r.Seq.GetStep(0, 2).Notes[0].Tie);       // clamped to the remaining 2 steps
        r.Seq.AddStepNote(0, 3, 62, 100, 9);
        Assert.Equal(1, r.Seq.GetStep(0, 3).Notes[0].Tie);
    }

    [Fact]
    public void RetriggeredSamePitchReplacesThePendingNoteOff()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 4); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        r.Seq.SetTie(0, 0, 3, 60);          // would end at 3.98 steps
        r.Seq.AddStepNote(0, 2, 60);        // retriggered at step 2
        r.Seq.Play();
        r.RenderSeconds(2.3);
        var offs = r.Of(SeqTapKind.NoteOff, 0).Where(e => e.Note == 60).ToList();
        double step = SeqRig.StepSamples(1, 120);
        Assert.Equal(1, offs.Count(o => o.Sample < 4 * step + 5));
        Assert.True(Math.Abs(offs[0].Sample - (2 + 0.98) * step) < 1.5);
    }

    [Fact]
    public void ProbabilityIsSeededAndDistributedAsConfigured()
    {
        int Run(uint seed, int prob)
        {
            using var r = new SeqRig();
            r.Engine.SetGlobal(GlobalParamId.Bpm, 250);
            r.Seq.SetTrackLength(0, 1); r.Seq.SetTrackStepNote(0, StepNote.ThirtySecond1_32);
            r.Seq.AddStepNote(0, 0, 60);
            r.Seq.SetStepProbability(0, 0, prob);
            r.Seq.Seed(seed);
            r.Seq.Play();
            r.Render((int)(SeqRig.StepSamples(0.125, 250) * 1500), block: 512);
            var steps = r.Of(SeqTapKind.Step, 0);
            Assert.True(steps.Count >= 1499);
            return steps.Count(s => s.Note == 1);
        }
        int a = Run(1234, 50), b = Run(1234, 50), c = Run(99, 50);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.InRange(a / 1500.0, 0.44, 0.56);
        Assert.InRange(Run(7, 25) / 1500.0, 0.18, 0.32);
        Assert.True(Run(7, 100) >= 1499);
        using var rr = new SeqRig();
        rr.Seq.SetStepProbability(0, 0, 0);
        Assert.Equal(25, rr.Seq.GetStep(0, 0).Probability);
        rr.Seq.SetStepProbability(0, 0, 500);
        Assert.Equal(100, rr.Seq.GetStep(0, 0).Probability);
    }

    [Fact]
    public void PLocksApplyForTheStepAndRestoreTheBaseValue()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 4); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        r.Engine.SetParam(0, ParamId.Harmonic, 32);
        r.Seq.AddStepNote(0, 0, 60); r.Seq.AddStepNote(0, 1, 60); r.Seq.AddStepNote(0, 2, 60);
        Assert.True(r.Seq.SetLock(0, 1, ParamId.Harmonic, 100));
        Assert.True(r.Seq.SetLock(0, 1, ParamId.Cutoff, 20));
        Assert.False(r.Seq.SetLock(0, 1, ParamId.Mute, 1));
        Assert.False(r.Seq.SetLock(0, 1, ParamId.SeqLength, 3));
        r.Seq.Play();
        double step = SeqRig.StepSamples(1, 120);
        var p = r.Engine.Parameters;
        r.Render((int)(step * 0.5));
        Assert.Equal(32f, p.Get(0, ParamId.Harmonic));
        r.Render((int)(step * 1.0));                                   // inside step 1
        Assert.Equal(100f, p.Get(0, ParamId.Harmonic));
        Assert.Equal(20f, p.Get(0, ParamId.Cutoff));
        Assert.Equal(32f, p.GetBase(0, ParamId.Harmonic));             // UI still sees the base value
        Assert.Equal(80f, p.GetBase(0, ParamId.Cutoff));
        r.Render((int)(step * 1.0));                                   // inside step 2: no lock -> restored
        Assert.Equal(32f, p.Get(0, ParamId.Harmonic));
        Assert.Equal(80f, p.Get(0, ParamId.Cutoff));
        Assert.False(p.HasOverride(0, ParamId.Harmonic));
    }

    [Fact]
    public void SoundLockSnapshotsTheSevenSoundParameters()
    {
        using var r = new SeqRig();
        var e = r.Engine;
        e.SetParam(0, ParamId.Wave, 8); e.SetParam(0, ParamId.Harmonic, 77); e.SetParam(0, ParamId.Balance, 90);
        e.SetParam(0, ParamId.Pitch, 24); e.SetParam(0, ParamId.Detune, 70); e.SetParam(0, ParamId.ModRate, 11); e.SetParam(0, ParamId.ModDepth, 55);
        r.Seq.SetSoundLock(0, 3);
        var locks = r.Seq.GetStep(0, 3).Locks.ToDictionary(l => l.Id, l => l.Value);
        Assert.Equal(7, locks.Count);
        Assert.Equal(8f, locks[ParamId.Wave]); Assert.Equal(77f, locks[ParamId.Harmonic]); Assert.Equal(90f, locks[ParamId.Balance]);
        Assert.Equal(24f, locks[ParamId.Pitch]); Assert.Equal(70f, locks[ParamId.Detune]);
        Assert.Equal(11f, locks[ParamId.ModRate]); Assert.Equal(55f, locks[ParamId.ModDepth]);
        // changing the knobs afterwards does not touch the locked step
        e.SetParam(0, ParamId.Harmonic, 5);
        Assert.Equal(77f, r.Seq.GetStep(0, 3).Locks.First(l => l.Id == ParamId.Harmonic).Value);
    }

    [Fact]
    public void RndSeqJumpsToRandomBlocksOfTheUnitSize()
    {
        foreach (var (unit, idx) in new[] { (4, 3), (8, 4), (2, 2), (16, 5) })
        {
            using var r = new SeqRig();
            r.Engine.SetGlobal(GlobalParamId.Bpm, 250);
            r.Seq.SetTrackLength(0, 32); r.Seq.SetTrackStepNote(0, StepNote.ThirtySecond1_32);
            r.Seq.SetTrackRndStep(0, idx);
            r.Seq.SetRndSeq(true);
            r.Seq.Seed(42);
            r.Seq.Play();
            r.Render((int)(SeqRig.StepSamples(0.125, 250) * 32 * 6), block: 700);
            var steps = r.Of(SeqTapKind.Step, 0).Select(e => e.Step).ToList();
            Assert.True(steps.Count >= 32 * 6 - 1);
            var starts = new HashSet<int>();
            for (int b = 0; b + unit <= steps.Count; b += unit)
            {
                Assert.Equal(0, steps[b] % unit);                          // blocks are aligned
                for (int k = 1; k < unit; k++) Assert.Equal(steps[b] + k, steps[b + k]);   // consecutive inside a block
                starts.Add(steps[b]);
            }
            Assert.True(starts.Count > 1, $"unit {unit}: all blocks identical");
        }
    }

    [Fact]
    public void RndSeqOffPlaysSequentially()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 8); r.Seq.SetTrackRndStep(0, 3);
        r.Seq.SetRndSeq(false);
        r.Seq.Play();
        r.RenderSeconds(2);
        var steps = r.Of(SeqTapKind.Step, 0).Select(e => e.Step).ToList();
        for (int i = 0; i < steps.Count; i++) Assert.Equal(i % 8, steps[i]);
    }

    [Fact]
    public void TransposeTakesEffectAtTheStartOfTheNextCycle()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 4); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        for (int s = 0; s < 4; s++) r.Seq.AddStepNote(0, s, 60);
        r.Seq.Play();
        double step = SeqRig.StepSamples(1, 120);
        r.Render((int)(step * 1.5));
        r.Seq.SetTranspose(5);
        r.Render((int)(step * 7));
        var ons = r.Of(SeqTapKind.NoteOn, 0).OrderBy(e => e.Sample).ToList();
        Assert.True(ons.Count >= 8);
        for (int i = 0; i < 4; i++) Assert.Equal(60, ons[i].Note);
        for (int i = 4; i < 8; i++) Assert.Equal(65, ons[i].Note);
        Assert.True(Math.Abs(ons[4].Sample - 4 * step) < 1.5);
        Assert.Equal(1, r.Of(SeqTapKind.CycleBoundary).Count(e => e.Sample < 4 * step + 2));
    }

    [Fact]
    public void CycleIsTheLongestTrackAndPatternBarsOverridesIt()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 4); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);       // 4 quarters
        r.Seq.SetTrackLength(1, 3); r.Seq.SetTrackStepNote(1, StepNote.Whole1_1);         // 12 quarters  <- master
        r.Seq.Play();
        r.RenderSeconds(13);
        var cb = r.Of(SeqTapKind.CycleBoundary);
        Assert.True(Math.Abs(cb[0].Sample - SeqRig.StepSamples(12, 120)) < 1.5);
        r.Seq.Stop(); r.Render(2000);
        r.Events.Clear();
        r.Seq.PatternBars = 1;
        r.Seq.Play();
        r.RenderSeconds(6);
        cb = r.Of(SeqTapKind.CycleBoundary);
        Assert.True(cb.Count >= 2);
        Assert.True(Math.Abs((cb[1].Sample - cb[0].Sample) - SeqRig.StepSamples(4, 120)) < 1.5);
    }

    [Fact]
    public void StopReleasesVoicesAtOnceWhileNaturalStopKeepsTheirTails()
    {
        int Voices(bool natural)
        {
            using var r = new SeqRig(waitForWaves: true);
            for (int l = 0; l < 3; l++) r.Engine.SetParam(l, ParamId.Release, 110);        // long release (~4 s)
            r.Seq.SetTrackLength(0, 4); r.Seq.SetTrackStepNote(0, StepNote.Whole1_1);
            r.Seq.SetTie(0, 0, 3, 48);
            r.Seq.Play();
            r.RenderSeconds(1.5);
            Assert.True(r.Engine.ActiveVoices >= 1);
            r.Events.Clear();
            if (natural) r.Seq.StopNatural(); else r.Seq.Stop();
            r.RenderSeconds(0.5);
            Assert.Equal(TransportState.Stopped, r.Seq.State);
            r.RenderSeconds(1.0);
            Assert.Empty(r.Of(SeqTapKind.NoteOn));
            return r.Engine.ActiveVoices;
        }
        Assert.Equal(0, Voices(natural: false));
        Assert.True(Voices(natural: true) >= 1);
    }

    [Fact]
    public void StopRestoresTheReleaseParameterAfterTheKill()
    {
        using var r = new SeqRig(waitForWaves: true);
        r.Engine.SetParam(0, ParamId.Release, 110);
        r.Seq.SetTie(0, 0, 3, 48);
        r.Seq.Play(); r.RenderSeconds(0.5);
        r.Seq.Stop(); r.RenderSeconds(0.1);
        Assert.True(r.Engine.Parameters.Get(0, ParamId.Release) < 10);      // fast release while killing
        r.RenderSeconds(0.5);
        Assert.Equal(110f, r.Engine.Parameters.Get(0, ParamId.Release));
    }

    [Fact]
    public void SeqMuteModeSilencesOnlyTheSequencerNotesAndSndModeSilencesTheLayer()
    {
        using var r = new SeqRig(waitForWaves: true);
        r.Engine.SetParam(0, ParamId.Mute, 1);
        r.Seq.SetTrackLength(0, 4);
        r.Seq.AddStepNote(0, 0, 60);
        // SEQ mode: the sequencer note is dropped, the keyboard plays
        r.Seq.MuteMode = MuteMode.Sequence;
        r.Seq.Play(); r.RenderSeconds(0.6);
        Assert.Equal(0, r.Engine.ActiveVoices);
        r.Engine.NoteOn(0, 64, 100); r.RenderSeconds(0.6);
        Assert.True(r.Engine.ActiveVoices >= 1);
        Assert.True(r.LastRms > 1e-4, $"rms {r.LastRms}");
        // SND mode (fresh rig, no reverb tail from before): the layer is silent for every source
        using var r2 = new SeqRig(waitForWaves: true);
        r2.Engine.SetParam(0, ParamId.Mute, 1);
        r2.Seq.MuteMode = MuteMode.Sound;
        r2.RenderSeconds(0.1);                                  // let the 10 ms mute ramp finish
        r2.Engine.NoteOn(0, 64, 100); r2.RenderSeconds(1.0);
        Assert.True(r2.LastRms < 1e-5, $"rms {r2.LastRms}");
    }
}

public class SequencerRecordingTests
{
    private static void PlayTo(SeqRig r, double steps, double step) => r.Render((int)(step * steps));

    [Fact]
    public void StepRecordingTogglesCopiesPastesAndClears()
    {
        using var r = new SeqRig();
        var s = r.Seq;
        Assert.True(s.SetStepNote(0, 2, 60, 90));
        Assert.True(s.SetStepNote(0, 2, 64));
        Assert.Equal(2, s.GetStep(0, 2).Notes.Count);
        Assert.False(s.SetStepNote(0, 2, 60));                  // toggled off
        Assert.Single(s.GetStep(0, 2).Notes);
        for (int n = 0; n < 7; n++) s.SetStepNote(0, 2, 70 + n);
        Assert.Equal(8, s.GetStep(0, 2).Notes.Count);
        Assert.False(s.SetStepNote(0, 2, 90));                  // a step holds at most 8 notes
        s.SetStepProbability(0, 2, 50); s.SetLock(0, 2, ParamId.Harmonic, 99);
        s.CopyStep(0, 2);
        Assert.True(s.PasteStep(0, 5));
        var c = s.GetStep(0, 5);
        Assert.Equal(8, c.Notes.Count); Assert.Equal(50, c.Probability); Assert.Equal(99f, c.Locks[0].Value);
        s.ClearStep(0, 2);
        Assert.False(s.GetStep(0, 2).HasNotes); Assert.False(s.GetStep(0, 2).HasLocks);
        Assert.Equal(100, s.GetStep(0, 2).Probability);
        s.ClearTrackNotes(0);
        Assert.False(s.GetStep(0, 5).HasNotes);
        Assert.True(s.GetStep(0, 5).HasLocks);                  // locks survive "clear notes"
        s.ClearLocks(0, 5);
        Assert.False(s.GetStep(0, 5).HasLocks);
        Assert.Equal(64, r.Seq.GetTrack(0).Steps.Count);
    }

    [Fact]
    public void AutoStepInputAdvancesTheCursorAndWraps()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(1, 3);
        Assert.Equal(0, r.Seq.StepInput(1, [60, 64, 67]));
        r.Seq.StepRest(1);
        Assert.Equal(2, r.Seq.StepInput(1, [62], tie: 5));
        Assert.Equal(0, r.Seq.StepCursor(1));
        Assert.Equal(3, r.Seq.GetStep(1, 0).Notes.Count);
        Assert.False(r.Seq.GetStep(1, 1).HasNotes);
        Assert.Equal(1, r.Seq.GetStep(1, 2).Notes[0].Tie);      // clamped: ties cannot wrap
    }

    [Fact]
    public void RealtimeRecordingQuantizesToTheNearestStepAndMeasuresTheTie()
    {
        using var r = new SeqRig(waitForWaves: true);
        double step = SeqRig.StepSamples(1, 120);
        r.Seq.SetTrackLength(0, 8); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        r.Seq.PreCountBeats = 0;
        r.Seq.RecordArmed = true;
        r.Seq.Play();
        PlayTo(r, 2.3, step);                                   // inside step 2, 30 % in: nearest step 2
        r.Seq.RecordNoteOn(0, 60, 100);
        PlayTo(r, 2.0, step);                                   // 4.3 -> note off ~2 steps later
        r.Seq.RecordNoteOff(0, 60);
        PlayTo(r, 0.4, step);                                   // 4.7: nearest step is 5
        r.Seq.RecordNoteOn(0, 64, 80);
        PlayTo(r, 0.1, step);
        r.Seq.RecordNoteOff(0, 64);
        var a = r.Seq.GetStep(0, 2);
        Assert.Single(a.Notes);
        Assert.Equal(new StepNoteData(60, 100, 2), a.Notes[0]);
        var b = r.Seq.GetStep(0, 5);
        Assert.Equal(new StepNoteData(64, 80, 1), b.Notes[0]);
    }

    [Fact]
    public void RealtimeRecordingCompensatesTheCurrentTranspose()
    {
        using var r = new SeqRig(waitForWaves: true);
        r.Seq.SetTrackLength(0, 8); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        r.Seq.SetTranspose(7);
        r.Seq.PreCountBeats = 0;
        r.Seq.RecordArmed = true;
        r.Seq.Play();
        r.RenderSeconds(0.1);
        r.Seq.RecordNoteOn(0, 67, 100);
        Assert.Equal(60, r.Seq.GetStep(0, 0).Notes[0].Note);
    }

    [Fact]
    public void RecordingNeedsRecArmedAndPlayback()
    {
        using var r = new SeqRig();
        r.Seq.RecordArmed = true;
        r.Seq.RecordNoteOn(0, 60, 100);                         // not playing
        Assert.False(r.Seq.GetStep(0, 0).HasNotes);
        r.Seq.RecordArmed = false;
        r.Seq.Play(); r.RenderSeconds(0.3);
        r.Seq.RecordNoteOn(0, 60, 100);
        Assert.False(r.Seq.GetStep(0, 0).HasNotes);
    }

    [Fact]
    public void OverwriteClearsPassedStepsExceptTheOnesRecordedInThisPass()
    {
        using var r = new SeqRig(waitForWaves: true);
        double step = SeqRig.StepSamples(1, 120);
        r.Seq.SetTrackLength(0, 8); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        for (int s = 0; s < 8; s++) r.Seq.AddStepNote(0, s, 50);
        r.Seq.PreCountBeats = 0;
        r.Seq.RecordArmed = true; r.Seq.OverwriteMode = true;
        r.Seq.Play();
        PlayTo(r, 1.1, step);
        r.Seq.RecordNoteOn(0, 72, 100);                         // step 1
        PlayTo(r, 3.0, step);                                   // playhead at 4.1
        r.Seq.RecordNoteOff(0, 72);
        var s1 = r.Seq.GetStep(0, 1);
        Assert.Single(s1.Notes); Assert.Equal(72, s1.Notes[0].Note);     // 50 replaced by 72
        Assert.False(r.Seq.GetStep(0, 2).HasNotes);
        Assert.False(r.Seq.GetStep(0, 3).HasNotes);
        Assert.True(r.Seq.GetStep(0, 5).HasNotes);                       // not reached yet
        Assert.True(r.Seq.GetStep(0, 7).HasNotes);
    }

    [Fact]
    public void DirectRecordingPutsNotesOnTheHeldStep()
    {
        using var r = new SeqRig();
        r.Seq.HoldStep(2, 6);
        r.Seq.RecordNoteOn(2, 60, 100);
        r.Seq.RecordNoteOn(2, 67, 90);
        r.Seq.ReleaseStep(2);
        r.Seq.RecordNoteOn(2, 72, 90);                          // no hold, not recording: ignored
        var st = r.Seq.GetStep(2, 6);
        Assert.Equal([60, 67], st.Notes.Select(n => n.Note).ToArray());
        Assert.False(r.Seq.GetStep(2, 0).HasNotes);
    }

    [Fact]
    public void NoteRouterListenerFeedsTheRecorder()
    {
        using var r = new SeqRig();
        var router = new NoteRouter(() => r.Engine, r.Engine.Parameters) { Listener = r.Seq };
        r.Seq.HoldStep(0, 3);
        router.NoteOn(NoteSource.Ui, 61, 100);
        router.NoteOff(NoteSource.Ui, 61);
        Assert.Equal(61, r.Seq.GetStep(0, 3).Notes[0].Note);
    }

    [Fact]
    public void ParameterRecordingWritesPLocksToTheCurrentStepAndLeavesTheBase()
    {
        using var r = new SeqRig();
        double step = SeqRig.StepSamples(1, 120);
        r.Seq.SetTrackLength(0, 4); r.Seq.SetTrackStepNote(0, StepNote.Quarter1_4);
        r.Engine.SetParam(0, ParamId.Harmonic, 30);
        r.Seq.ParamRecordArmed = true;
        r.Seq.Play();
        PlayTo(r, 2.5, step);
        float v = r.Seq.SetLayerParam(0, ParamId.Harmonic, 111);
        Assert.Equal(111f, v);
        Assert.Equal(111f, r.Seq.GetStep(0, 2).Locks.Single(l => l.Id == ParamId.Harmonic).Value);
        Assert.Equal(30f, r.Engine.Parameters.GetBase(0, ParamId.Harmonic));
        r.Render(64);
        Assert.Equal(111f, r.Engine.Parameters.Get(0, ParamId.Harmonic));       // audible at once
        PlayTo(r, 1.0, step);                                                   // step 3: restored
        Assert.Equal(30f, r.Engine.Parameters.Get(0, ParamId.Harmonic));
        // not lockable / not armed -> base value path
        r.Seq.SetLayerParam(0, ParamId.Mute, 1);
        Assert.Equal(1f, r.Engine.Parameters.GetBase(0, ParamId.Mute));
        r.Seq.ParamRecordArmed = false;
        r.Seq.SetLayerParam(0, ParamId.Cutoff, 10);
        Assert.Equal(10f, r.Engine.Parameters.GetBase(0, ParamId.Cutoff));
    }

    [Fact]
    public async Task DirectPLockOfAWaveRequestsABuild()
    {
        using var r = new SeqRig();
        Assert.False(r.Engine.Bank.IsReady(27));
        r.Seq.SetLock(1, 0, ParamId.Wave, 27);
        await r.Engine.Bank.EnsureBuiltAsync(27);
        Assert.True(r.Engine.Bank.IsReady(27));
    }

    [Fact]
    public void DuplicateTrackExtendsLengthByCopying()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 16);
        r.Seq.AddStepNote(0, 0, 60); r.Seq.AddStepNote(0, 7, 67); r.Seq.SetLock(0, 3, ParamId.Cutoff, 55);
        Assert.Equal(32, r.Seq.DuplicateTrack(0, 2));
        Assert.Equal(32, r.Seq.GetTrack(0).Length);
        Assert.Equal(60, r.Seq.GetStep(0, 16).Notes[0].Note);
        Assert.Equal(67, r.Seq.GetStep(0, 23).Notes[0].Note);
        Assert.Equal(55f, r.Seq.GetStep(0, 19).Locks[0].Value);
        Assert.Equal(64, r.Seq.DuplicateTrack(0, 4));          // capped at 64
        Assert.Equal(60, r.Seq.GetStep(0, 48).Notes[0].Note);
        r.Seq.SetTrackLength(1, 10);
        r.Seq.AddStepNote(1, 2, 61);
        Assert.Equal(40, r.Seq.DuplicateTrack(1, 4));
        Assert.Equal(61, r.Seq.GetStep(1, 32).Notes[0].Note);
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Seq.DuplicateTrack(1, 3));
    }

    [Fact]
    public void CopyAndPasteTrack()
    {
        using var r = new SeqRig();
        r.Seq.AddStepNote(0, 4, 60);
        r.Seq.CopyTrack(0);
        Assert.True(r.Seq.PasteTrack(2));
        Assert.Equal(60, r.Seq.GetStep(2, 4).Notes[0].Note);
        r.Seq.ClearTrack(0);
        Assert.False(r.Seq.GetStep(0, 4).HasNotes);
        Assert.True(r.Seq.GetStep(2, 4).HasNotes);
    }

    [Fact]
    public void PreCountDelaysThePlaybackByTheCountedBeatsAndClicks()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 4); r.Seq.AddStepNote(0, 0, 60);
        r.Seq.RecordArmed = true; r.Seq.PreCountBeats = 3; r.Seq.MetronomeVolume = 10;
        r.Seq.Play();
        r.Render(1000);
        Assert.Equal(TransportState.PreCount, r.Seq.State);
        r.RenderSeconds(2);
        double beat = SeqRig.StepSamples(1, 120);
        var first = r.Of(SeqTapKind.NoteOn, 0)[0];
        Assert.True(Math.Abs(first.Sample - 3 * beat) < 1.5);
        Assert.Equal(TransportState.Playing, r.Seq.State);
        Assert.True(r.Of(SeqTapKind.Beat).Count >= 4);
    }

    [Fact]
    public void MetronomeClicksAreMixedAfterTheFxAndFollowVolumeAndSwitch()
    {
        double Rms(bool on, int vol)
        {
            using var r = new SeqRig();
            r.Seq.MetronomeOn = on; r.Seq.MetronomeVolume = vol;
            r.Seq.Play();
            r.RenderSeconds(2);
            return r.LastRms;
        }
        Assert.True(Rms(false, 15) < 1e-9);
        double lo = Rms(true, 4), hi = Rms(true, 15);
        Assert.True(lo > 1e-4);
        Assert.True(hi > lo * 2);
        Assert.True(Rms(true, 0) < 1e-9);
    }
}

public class ParameterOverlayAndThreadingTests
{
    [Fact]
    public void OverridesHideTheBaseForReadersButNotForTheUi()
    {
        var p = new ParameterStore();
        p.Set(1, ParamId.Cutoff, 70);
        p.SetOverride(1, ParamId.Cutoff, 20);
        Assert.Equal(20f, p.Get(1, ParamId.Cutoff));
        Assert.Equal(70f, p.GetBase(1, ParamId.Cutoff));
        p.Set(1, ParamId.Cutoff, 90);                               // knob moves: base only
        Assert.Equal(20f, p.Get(1, ParamId.Cutoff));
        Assert.Equal(90f, p.GetBase(1, ParamId.Cutoff));
        p.SetOverride(1, ParamId.Cutoff, 500);                      // overrides are clamped like base values
        Assert.Equal(127f, p.Get(1, ParamId.Cutoff));
        Assert.True(p.HasOverride(1, ParamId.Cutoff));
        Assert.False(p.HasOverride(0, ParamId.Cutoff));
        p.SetOverride(1, ParamId.Pan, 5); p.ClearOverrides(1);
        Assert.Equal(90f, p.Get(1, ParamId.Cutoff));
        Assert.Equal(0f, p.Get(1, ParamId.Pan));
    }

    [Fact]
    public void MuteModeIsAGlobalParameterWithTwoValues()
    {
        var p = new ParameterStore();
        Assert.Equal(0f, p.Get(GlobalParamId.MuteMode));
        Assert.Equal(1f, p.Set(GlobalParamId.MuteMode, 7));
    }

    [Fact]
    public void EditingFromAnotherThreadWhileRenderingNeverBreaksPlayback()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 16); r.Seq.SetTrackStepNote(0, StepNote.ThirtySecond1_32);
        r.Engine.SetGlobal(GlobalParamId.Bpm, 200);
        for (int s = 0; s < 16; s++) r.Seq.AddStepNote(0, s, 60 + s);
        r.Seq.Play();
        r.Seq.Tap = null;
        using var stop = new CancellationTokenSource();
        Exception? error = null;
        var ui = new Thread(() =>
        {
            var rng = new Random(5);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    int s = rng.Next(16);
                    r.Seq.SetStepNote(0, s, 40 + rng.Next(40));
                    r.Seq.SetLock(0, s, ParamId.Harmonic, rng.Next(128));
                    if (rng.Next(8) == 0) r.Seq.ClearStep(0, rng.Next(16));
                    r.Seq.Poll();
                    r.Seq.GetTrack(0);
                }
            }
            catch (Exception e) { error = e; }
        });
        ui.Start();
        var buf = new float[512];
        for (int i = 0; i < 1500; i++)
        {
            r.Engine.Render(buf, 256);
            foreach (var v in buf) Assert.True(float.IsFinite(v));
        }
        stop.Cancel(); ui.Join();
        Assert.Null(error);
        Assert.Equal(TransportState.Playing, r.Seq.State);
        Assert.Equal(0, r.Seq.DroppedCommands);
    }
}
