using Sambient.Dsp.Params;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;

namespace Sambient.Tests;

/// <summary>Voice modes (POLY, MONO, LEGATO, UNISON, ARP) and HOLD. Assertions are on voice state, not audio frequency.</summary>
public class VoiceModeTests
{
    private const int Fs = 48000;
    private static readonly WavetableBank SharedBank = MakeBank();

    private static WavetableBank MakeBank()
    {
        var b = new WavetableBank();
        foreach (int w in new[] { 1, 2, 3 }) b.EnsureBuiltAsync(w).GetAwaiter().GetResult();
        return b;
    }

    private static AudioEngine Make(VoiceMode mode, int layer = 0)
    {
        var p = new ParameterStore();
        p.Set(layer, ParamId.Wave, 1);
        p.Set(layer, ParamId.Attack, 0);
        p.Set(layer, ParamId.Release, 20);
        p.Set(layer, ParamId.VoiceMode, (float)mode);
        p.Set(GlobalParamId.Bpm, 120);
        return new AudioEngine(Fs, p, SharedBank);
    }

    private static void Run(AudioEngine e, int frames, int block = 256)
    {
        var buf = new float[block * 2];
        for (int done = 0; done < frames; done += block) e.Render(buf, Math.Min(block, frames - done));
    }

    private static SynthVoice[] Voices(AudioEngine e, int layer = 0) => e.GetLayer(layer).VoicesForTest;
    private static SynthVoice[] Gated(AudioEngine e) => Voices(e).Where(v => v.Active && v.Gate).ToArray();
    private static int ActiveCount(AudioEngine e) => Voices(e).Count(v => v.Active);

    // ------------------------------------------------------------ POLY

    [Fact]
    public void Poly_UnchangedBehaviour()
    {
        var e = Make(VoiceMode.Poly);
        e.NoteOn(0, 60, 100); e.NoteOn(0, 64, 100); e.NoteOn(0, 67, 100);
        Run(e, 512);
        Assert.Equal(3, Gated(e).Length);
        Assert.All(Gated(e), v => { Assert.Equal(0f, v.DetuneOffsetCents); Assert.Equal(0f, v.PanSpread); });
        e.NoteOff(0, 64);
        Run(e, 512);
        Assert.Equal(2, Gated(e).Length);
        Assert.Equal(1, e.GetLayer(0).VoicesForNote(60));
        e.NoteOn(0, 60, 90);                       // same note reuses its voice
        Run(e, 256);
        Assert.Equal(1, e.GetLayer(0).VoicesForNote(60));
    }

    [Fact]
    public void Poly_SustainPedalStillDefersNoteOff()
    {
        var e = Make(VoiceMode.Poly);
        e.NoteOn(0, 60, 100);
        Run(e, 256);
        e.Sustain(true); e.NoteOff(0, 60);
        Run(e, 256);
        Assert.Single(Gated(e));
        e.Sustain(false);
        Run(e, 256);
        Assert.Empty(Gated(e));
    }

    // ------------------------------------------------------------ MONO / LEGATO

    [Fact]
    public void Mono_LastNotePriorityWithStack_AndRetrigger()
    {
        var e = Make(VoiceMode.Mono);
        e.NoteOn(0, 60, 100); Run(e, 256);
        var v = Voices(e)[0];
        long age1 = v.Age;
        e.NoteOn(0, 64, 100); Run(e, 256);
        Assert.Equal(64, v.Note);
        Assert.True(v.Age > age1);                          // retriggered
        Assert.Equal(1, ActiveCount(e));
        e.NoteOn(0, 67, 100); Run(e, 256);
        Assert.Equal(67, v.Note);
        long age3 = v.Age;
        e.NoteOff(0, 64); Run(e, 256);                      // not the sounding note: nothing changes
        Assert.Equal(67, v.Note); Assert.Equal(age3, v.Age);
        e.NoteOff(0, 67); Run(e, 256);                      // back to 60 (64 is gone), retriggered
        Assert.Equal(60, v.Note); Assert.True(v.Gate); Assert.True(v.Age > age3);
        e.NoteOff(0, 60); Run(e, 256);
        Assert.False(v.Gate);
    }

    [Fact]
    public void Mono_ReturnsToPreviousHeldNote()
    {
        var e = Make(VoiceMode.Mono);
        e.NoteOn(0, 60, 100); e.NoteOn(0, 64, 100); Run(e, 256);
        e.NoteOff(0, 64); Run(e, 256);
        Assert.Equal(60, Voices(e)[0].Note);
        Assert.True(Voices(e)[0].Gate);
    }

    [Fact]
    public void Mono_GlideMsFollowsParameterAndAppliesToNextSlides()
    {
        var e = Make(VoiceMode.Mono);
        e.SetParam(0, ParamId.Glide, 60);
        e.NoteOn(0, 60, 100); Run(e, 256);
        e.NoteOn(0, 72, 100); Run(e, 256);
        float g1 = Voices(e)[0].GlideMs;
        Assert.Equal(ParamCurves.GlideMs(60), g1, 3);
        Assert.True(g1 > 0);
        e.SetParam(0, ParamId.Glide, 100);
        e.NoteOn(0, 48, 100); Run(e, 256);
        Assert.Equal(ParamCurves.GlideMs(100), Voices(e)[0].GlideMs, 3);
        Assert.Equal(48, Voices(e)[0].Note);
    }

    [Fact]
    public void Legato_OverlapDoesNotRetrigger_AndReturnsToPrevious()
    {
        var e = Make(VoiceMode.Legato);
        e.NoteOn(0, 60, 100); Run(e, 256);
        var v = Voices(e)[0];
        long age = v.Age;
        e.NoteOn(0, 64, 100); Run(e, 256);
        Assert.Equal(64, v.Note); Assert.Equal(age, v.Age);   // no retrigger
        Assert.Equal(1, ActiveCount(e));
        e.NoteOff(0, 64); Run(e, 256);
        Assert.Equal(60, v.Note); Assert.Equal(age, v.Age);
        e.NoteOff(0, 60); Run(e, 48000);
        Assert.False(v.Active);
        e.NoteOn(0, 65, 100); Run(e, 256);                    // after all keys released: fresh start
        Assert.True(v.Active && v.Gate); Assert.Equal(65, v.Note);
        Assert.True(v.Age > age);
    }

    [Fact]
    public void Mono_SustainPedalKeepsNoteUntilPedalUp()
    {
        var e = Make(VoiceMode.Mono);
        e.NoteOn(0, 60, 100); Run(e, 256);
        e.Sustain(true); e.NoteOff(0, 60); Run(e, 256);
        Assert.True(Voices(e)[0].Gate);
        e.Sustain(false); Run(e, 256);
        Assert.False(Voices(e)[0].Gate);
    }

    // ------------------------------------------------------------ UNISON

    [Fact]
    public void Unison_FourVoicesPerNote_SymmetricDetuneAndPan()
    {
        var e = Make(VoiceMode.Unison);
        e.SetParam(0, ParamId.UnisonDetune, 127);
        e.NoteOn(0, 60, 100); Run(e, 256);
        var g = Gated(e);
        Assert.Equal(4, g.Length);
        Assert.All(g, v => Assert.Equal(60, v.Note));
        float cents = ParamCurves.UnisonDetuneCents(127);
        Assert.Equal(20f, cents, 3);
        var det = g.Select(v => v.DetuneOffsetCents).OrderBy(x => x).ToArray();
        Assert.Equal(-cents, det[0], 3); Assert.Equal(cents, det[3], 3);
        Assert.Equal(0f, det.Sum(), 3);
        for (int i = 0; i < 2; i++) Assert.Equal(-det[3 - i], det[i], 3);
        var pan = g.Select(v => v.PanSpread).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { -0.6f, -0.2f, 0.2f, 0.6f }, pan);
    }

    [Fact]
    public void Unison_DetuneFollowsParameter()
    {
        var e = Make(VoiceMode.Unison);
        e.SetParam(0, ParamId.UnisonDetune, 64);
        e.NoteOn(0, 60, 100); Run(e, 256);
        float cents = ParamCurves.UnisonDetuneCents(64);
        Assert.Equal(cents, Gated(e).Max(v => v.DetuneOffsetCents), 3);
    }

    [Fact]
    public void Unison_PolyphonyIsFourNotes_StealsWholeGroup()
    {
        var e = Make(VoiceMode.Unison);
        for (int n = 0; n < 4; n++) { e.NoteOn(0, 60 + n, 100); Run(e, 64); }
        Assert.Equal(16, ActiveCount(e));
        e.NoteOn(0, 72, 100); Run(e, 2048);                  // fifth note steals the oldest group (note 60)
        Assert.Equal(16, ActiveCount(e));
        Assert.Equal(0, e.GetLayer(0).VoicesForNote(60));
        Assert.Equal(4, e.GetLayer(0).VoicesForNote(72));
        for (int n = 1; n < 4; n++) Assert.Equal(4, e.GetLayer(0).VoicesForNote(60 + n));
        e.NoteOff(0, 72); Run(e, 256);
        Assert.Equal(12, Gated(e).Length);
    }

    [Fact]
    public void Unison_SameNoteAgainRetriggersItsGroup()
    {
        var e = Make(VoiceMode.Unison);
        e.NoteOn(0, 60, 100); Run(e, 256);
        long age = Voices(e).First(v => v.Active).Age;
        e.NoteOn(0, 60, 100); Run(e, 256);
        Assert.Equal(4, ActiveCount(e));
        Assert.True(Voices(e).First(v => v.Active).Age > age);
    }

    [Fact]
    public void NoiseLayer_TreatsUnisonAsPoly()
    {
        var p = new ParameterStore();
        p.Set(3, ParamId.VoiceMode, (float)VoiceMode.Unison);
        var e = new AudioEngine(Fs, p, SharedBank);
        e.NoteOn(3, 60, 100); Run(e, 256);
        Assert.Equal(1, e.NoiseLayer.ActiveVoices);
        Assert.Equal(VoiceMode.Poly, e.NoiseLayer.Modes.Mode);
    }

    [Fact]
    public void NoiseLayer_MonoWorks()
    {
        var p = new ParameterStore();
        p.Set(3, ParamId.VoiceMode, (float)VoiceMode.Mono);
        var e = new AudioEngine(Fs, p, SharedBank);
        e.NoteOn(3, 60, 100); e.NoteOn(3, 64, 100); Run(e, 256);
        Assert.Equal(1, e.NoiseLayer.ActiveVoices);
        Assert.Equal(64, e.NoiseLayer.VoicesForTest[0].Note);
        e.NoteOff(3, 64); Run(e, 256);
        Assert.Equal(60, e.NoiseLayer.VoicesForTest[0].Note);
    }

    // ------------------------------------------------------------ mode switching

    [Theory]
    [InlineData(VoiceMode.Poly, VoiceMode.Mono)]
    [InlineData(VoiceMode.Poly, VoiceMode.Unison)]
    [InlineData(VoiceMode.Unison, VoiceMode.Poly)]
    [InlineData(VoiceMode.Mono, VoiceMode.Arp)]
    [InlineData(VoiceMode.Arp, VoiceMode.Poly)]
    [InlineData(VoiceMode.Legato, VoiceMode.Unison)]
    public void ModeSwitchWhileHeld_NoStuckNotes(VoiceMode from, VoiceMode to)
    {
        var e = Make(from);
        e.NoteOn(0, 60, 100); e.NoteOn(0, 64, 100); e.NoteOn(0, 67, 100);
        Run(e, 2000);
        e.SetParam(0, ParamId.VoiceMode, (float)to);
        Run(e, 2000);
        e.NoteOff(0, 60); e.NoteOff(0, 64); e.NoteOff(0, 67);
        Run(e, 1000);
        Assert.Empty(Gated(e));
        Run(e, 48000);
        Assert.Equal(0, ActiveCount(e));
        Assert.Equal(0, e.GetLayer(0).Modes.AliveCount);
    }

    [Fact]
    public void ModeSwitch_ReleasedKeysAreNotRevived()
    {
        var e = Make(VoiceMode.Poly);
        e.NoteOn(0, 60, 100); e.NoteOn(0, 64, 100); Run(e, 512);
        e.NoteOff(0, 60); Run(e, 100);
        e.SetParam(0, ParamId.VoiceMode, (float)VoiceMode.Mono); Run(e, 512);
        Assert.Equal(64, Voices(e)[0].Note);
        Assert.Single(Gated(e));
    }

    [Fact]
    public void AllNotesOff_ReleasesEverythingInEveryMode()
    {
        foreach (var m in new[] { VoiceMode.Poly, VoiceMode.Mono, VoiceMode.Legato, VoiceMode.Unison, VoiceMode.Arp })
        {
            var e = Make(m);
            e.NoteOn(0, 60, 100); e.NoteOn(0, 64, 100); Run(e, 5000);
            e.AllNotesOff(); Run(e, 256);
            Assert.Empty(Gated(e));
            Run(e, 48000);
            Assert.Equal(0, ActiveCount(e));
        }
    }

    // ------------------------------------------------------------ ARP

    private static readonly int[] Chord = { 60, 64, 67 };

    public static IEnumerable<object[]> ArpExpectations() => new[]
    {
        new object[] { ArpType.Up, new[] { 60, 64, 67 } },
        new object[] { ArpType.Down, new[] { 67, 64, 60 } },
        new object[] { ArpType.UpDown, new[] { 60, 64, 67, 64 } },
        new object[] { ArpType.DownUp, new[] { 67, 64, 60, 64 } },
        new object[] { ArpType.UpAndDown, new[] { 60, 64, 67, 67, 64, 60 } },
        new object[] { ArpType.DownAndUp, new[] { 67, 64, 60, 60, 64, 67 } },
        new object[] { ArpType.UpPlus1, new[] { 60, 64, 67, 72, 76, 79 } },
        new object[] { ArpType.UpPlus2, new[] { 60, 64, 67, 72, 76, 79, 84, 88, 91 } },
        new object[] { ArpType.DownMinus1, new[] { 67, 64, 60, 55, 52, 48 } },
        new object[] { ArpType.DownMinus2, new[] { 67, 64, 60, 55, 52, 48, 43, 40, 36 } },
    };

    [Theory]
    [MemberData(nameof(ArpExpectations))]
    public void Arp_PlaysExpectedOrder(ArpType type, int[] expected)
    {
        var e = Make(VoiceMode.Arp);
        e.SetParam(0, ParamId.ArpType, (float)type);
        foreach (int n in Chord) e.NoteOn(0, n, 100);
        var voice = Voices(e)[0];
        var seen = new List<int>();
        int steps = expected.Length * 2;
        Run(e, 3000);                                          // step 0 note-on at sample 0, gate until 3600
        for (int s = 0; s < steps; s++)
        {
            Assert.True(voice.Gate);
            seen.Add(voice.Note);
            Run(e, 6000);
        }
        for (int s = 0; s < steps; s++) Assert.Equal(expected[s % expected.Length], seen[s]);
    }

    [Fact]
    public void Arp_PlayOrderFollowsPressOrder()
    {
        var e = Make(VoiceMode.Arp);
        e.SetParam(0, ParamId.ArpType, (float)ArpType.PlayOrder);
        foreach (int n in new[] { 67, 60, 64 }) e.NoteOn(0, n, 100);
        var voice = Voices(e)[0];
        var seen = new List<int>();
        Run(e, 3000);
        for (int s = 0; s < 6; s++) { seen.Add(voice.Note); Run(e, 6000); }
        Assert.Equal(new[] { 67, 60, 64, 67, 60, 64 }, seen);
    }

    [Fact]
    public void Arp_RandomPlaysHeldNotesOnly()
    {
        var e = Make(VoiceMode.Arp);
        e.SetParam(0, ParamId.ArpType, (float)ArpType.Random);
        foreach (int n in Chord) e.NoteOn(0, n, 100);
        var voice = Voices(e)[0];
        var seen = new List<int>();
        Run(e, 3000);
        for (int s = 0; s < 24; s++) { seen.Add(voice.Note); Run(e, 6000); }
        Assert.Equal(24, seen.Count);
        Assert.All(seen, n => Assert.Contains(n, Chord));
        Assert.True(seen.Distinct().Count() > 1);
    }

    [Fact]
    public void Arp_NoteTypesBuildSequencesWithoutHeldNotes()
    {
        var o = new int[200]; var v = new int[200];
        foreach (ArpType t in Enum.GetValues<ArpType>())
            Assert.Equal(0, Arpeggiator.Build(t, ReadOnlySpan<int>.Empty, ReadOnlySpan<int>.Empty, o, v));
        // single note: U.D / D.U degrade to that note
        Assert.Equal(1, Arpeggiator.Build(ArpType.UpDown, new[] { 60 }, new[] { 100 }, o, v));
        // octave transposition is clipped to MIDI range
        int len = Arpeggiator.Build(ArpType.UpPlus2, new[] { 120 }, new[] { 100 }, o, v);
        Assert.Equal(1, len);
    }

    [Theory]
    [InlineData(ArpType.Up, (int)StepNote.Sixteenth1_16, 120f, 6000.0)]
    [InlineData(ArpType.Up, (int)StepNote.QuarterTriplet, 120f, 16000.0)]
    [InlineData(ArpType.Up, (int)StepNote.Quarter1_4, 90f, 32000.0)]
    public void Arp_StepTimingIsSampleAccurate(ArpType type, int stepNote, float bpm, double expectedStep)
    {
        var arp = new Arpeggiator { SampleRate = Fs, Bpm = bpm, Type = type, StepQuarters = Arpeggiator.StepQuarterNotes[stepNote] };
        arp.SetNotes(Chord, new[] { 100, 100, 100 });
        arp.Start();
        var onsets = new List<long>();
        long t = 0;
        while (onsets.Count < 8)
        {
            while (arp.TryNextEvent(out var ev)) if (ev.IsNoteOn) onsets.Add(t);
            int n = arp.SamplesToNextEvent;
            Assert.True(n >= 1);
            arp.Advance(n); t += n;
        }
        Assert.Equal(0, onsets[0]);
        for (int i = 1; i < onsets.Count; i++)
            Assert.InRange(onsets[i] - i * arp.StepSamples, -1.0, 1.0);
        Assert.InRange(arp.StepSamples, expectedStep - 1.0, expectedStep + 1.0);
    }

    [Fact]
    public void Arp_TimingInsideEngineIsIndependentOfBlockSize()
    {
        // 1/16 at 120 BPM = 6000 samples: the first voice start happens at sample 0, the second step's note at 6000.
        foreach (int block in new[] { 64, 333, 1024 })
        {
            var e = Make(VoiceMode.Arp);
            foreach (int n in Chord) e.NoteOn(0, n, 100);
            var voice = Voices(e)[0];
            var buf = new float[block * 2];
            int total = 0; long prevAge = -1; var starts = new List<int>();
            while (total < 24000)
            {
                e.Render(buf, block);
                total += block;
                if (voice.Age != prevAge) { prevAge = voice.Age; starts.Add(total); }
            }
            // each start is detected at the end of the block that contains it
            for (int i = 0; i < Math.Min(starts.Count, 4); i++)
                Assert.InRange(starts[i] - (i * 6000), 1, block);
            Assert.True(starts.Count >= 4);
        }
    }

    [Fact]
    public void Arp_ReleasingAllKeysStopsTheArp()
    {
        var e = Make(VoiceMode.Arp);
        foreach (int n in Chord) e.NoteOn(0, n, 100);
        Run(e, 3000);
        Assert.True(Voices(e)[0].Gate);
        foreach (int n in Chord) e.NoteOff(0, n);
        Run(e, 256);
        Assert.False(Voices(e)[0].Gate);
        Assert.False(e.GetLayer(0).Modes.Arp.Running);
        long age = Voices(e)[0].Age;
        Run(e, 48000);
        Assert.Equal(age, Voices(e)[0].Age);                 // nothing retriggered
        Assert.Equal(0, ActiveCount(e));
        // pressing a key restarts it immediately
        e.NoteOn(0, 62, 100); Run(e, 256);
        Assert.True(Voices(e)[0].Gate); Assert.Equal(62, Voices(e)[0].Note);
    }

    [Fact]
    public void Arp_GateEndsBeforeNextStep()
    {
        var e = Make(VoiceMode.Arp);
        e.NoteOn(0, 60, 100);
        Run(e, 3000);
        Assert.True(Voices(e)[0].Gate);
        Run(e, 1500);                                         // 4500 > 60% of 6000
        Assert.False(Voices(e)[0].Gate);
        Run(e, 1600);                                         // 6100: next note on
        Assert.True(Voices(e)[0].Gate);
    }

    [Fact]
    public void Arp_ZeroAllocationsInRender()
    {
        var e = Make(VoiceMode.Arp);
        e.SetParam(0, ParamId.ArpType, (float)ArpType.UpPlus2);
        foreach (int n in Chord) e.NoteOn(0, n, 100);
        var buf = new float[512 * 2];
        for (int i = 0; i < 200; i++) e.Render(buf, 512);     // warm up (JIT, tiers)
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 400; i++) e.Render(buf, 512);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    // ------------------------------------------------------------ HOLD

    [Fact]
    public void Hold_LatchesKeyAfterRelease_ToggleReleases()
    {
        var e = Make(VoiceMode.Poly);
        e.HoldToggle(0, 60, 100); e.NoteOn(0, 64, 100);       // 60 latched (OK + key), 64 plain
        Run(e, 512);
        e.NoteOff(0, 60); e.NoteOff(0, 64); Run(e, 2000);
        Assert.Single(Gated(e)); Assert.Equal(60, Gated(e)[0].Note);
        Run(e, 20000);
        Assert.Single(Gated(e));                               // still sounding
        e.HoldToggle(0, 60, 100); Run(e, 256);                 // same key again releases
        Assert.Empty(Gated(e));
    }

    [Fact]
    public void Hold_OnAlreadyHeldKeyLatchesIt()
    {
        var e = Make(VoiceMode.Poly);
        e.NoteOn(0, 60, 100); Run(e, 256);
        e.HoldToggle(0, 60, 100); Run(e, 256);
        e.NoteOff(0, 60); Run(e, 2000);
        Assert.Single(Gated(e));
        Assert.True(e.GetLayer(0).Modes.IsLatched(60));
    }

    [Fact]
    public void ClearHolds_ReleasesLatchedKeysOfOneLayerOnly()
    {
        var e = Make(VoiceMode.Poly);
        e.HoldToggle(0, 60, 100); e.HoldToggle(0, 64, 100); e.HoldToggle(1, 62, 100);
        Run(e, 256);
        e.NoteOff(0, 60); e.NoteOff(0, 64); e.NoteOff(1, 62);
        Run(e, 256);
        Assert.Equal(2, Gated(e).Length);
        e.ClearHolds(0); Run(e, 256);
        Assert.Empty(Gated(e));
        Assert.Single(Voices(e, 1), v => v.Active && v.Gate);
        e.ClearAllHolds(); Run(e, 256);
        Assert.DoesNotContain(Voices(e, 1), v => v.Active && v.Gate);
    }

    [Fact]
    public void ClearHolds_KeepsPhysicallyHeldKeys()
    {
        var e = Make(VoiceMode.Poly);
        e.NoteOn(0, 60, 100); e.HoldToggle(0, 60, 100); e.HoldToggle(0, 64, 100);
        Run(e, 256);
        e.NoteOff(0, 64); Run(e, 256);
        e.ClearHolds(0); Run(e, 256);
        Assert.Single(Gated(e)); Assert.Equal(60, Gated(e)[0].Note);   // key 60 is still pressed
    }

    [Fact]
    public void Hold_InArpModeKeepsNotesInTheArp()
    {
        var e = Make(VoiceMode.Arp);
        foreach (int n in Chord) e.HoldToggle(0, n, 100);
        foreach (int n in Chord) e.NoteOff(0, n);
        Run(e, 3000);
        var voice = Voices(e)[0];
        var seen = new List<int>();
        for (int s = 0; s < 6; s++) { seen.Add(voice.Note); Run(e, 6000); }
        Assert.Equal(new[] { 60, 64, 67, 60, 64, 67 }, seen);
        e.HoldToggle(0, 64, 100);                              // unlatch one
        Run(e, 12000);
        Assert.Equal(2, e.GetLayer(0).Modes.AliveCount);
        e.ClearHolds(0); Run(e, 512);
        Assert.False(e.GetLayer(0).Modes.Arp.Running);
        Assert.False(voice.Gate);
    }

    [Fact]
    public void Hold_InMonoModeIsTheNewestNote()
    {
        var e = Make(VoiceMode.Mono);
        e.HoldToggle(0, 60, 100); e.NoteOff(0, 60); Run(e, 2000);
        Assert.True(Voices(e)[0].Gate);
        e.NoteOn(0, 64, 100); Run(e, 256);
        e.NoteOff(0, 64); Run(e, 256);
        Assert.Equal(60, Voices(e)[0].Note);                   // returns to the latched note
        Assert.True(Voices(e)[0].Gate);
        e.ClearHolds(0); Run(e, 256);
        Assert.False(Voices(e)[0].Gate);
    }

    [Fact]
    public void Hold_SustainPedalIsIndependent()
    {
        var e = Make(VoiceMode.Poly);
        e.HoldToggle(0, 60, 100); e.NoteOn(0, 64, 100); Run(e, 256);
        e.Sustain(true); e.NoteOff(0, 60); e.NoteOff(0, 64); Run(e, 256);
        e.Sustain(false); Run(e, 2000);
        Assert.Single(Gated(e)); Assert.Equal(60, Gated(e)[0].Note);   // pedal released 64, hold keeps 60
    }
}
