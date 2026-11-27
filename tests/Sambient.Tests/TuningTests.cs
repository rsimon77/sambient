using Sambient.Dsp.Noise;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;

namespace Sambient.Tests;

/// <summary>M4: tuning systems (temperaments, keys, master tune, TUNE ALL, special concert pitches) and their use by the voices.</summary>
public class TuningTests
{
    private const int Fs = 48000;
    private static readonly WavetableBank Bank = MakeBank();
    private static WavetableBank MakeBank() { var b = new WavetableBank(); b.EnsureBuiltAsync(3).GetAwaiter().GetResult(); return b; }

    private static double Hz(Temperament t, int key, int note, float master = 0)
    {
        var tu = new Tuning();
        tu.Update(t, key, master);
        return tu.HzFor(note);
    }
    private static double Cents(double a, double b) => 1200.0 * Math.Log2(a / b);
    private static double C4(Temperament t, int key, float master = 0) => Hz(t, key, 60, master);

    // ------------------------------------------------------------------ table maths

    [Fact]
    public void Equal_A4Is440_AndMasterTuneShiftsInCents()
    {
        Assert.Equal(440.0, Hz(Temperament.Equal, 0, 69), 3);
        Assert.Equal(261.6255653, Hz(Temperament.Equal, 0, 60), 4);
        Assert.Equal(0.0, Cents(Hz(Temperament.Equal, 0, 69, 75), 440.0 * Math.Pow(2, 75 / 1200.0)), 4);
        Assert.Equal(75.0, Cents(Hz(Temperament.Equal, 0, 69, 75), 440.0), 3);
        Assert.Equal(-75.0, Cents(Hz(Temperament.Equal, 0, 33, -75), 55.0), 3);
        var t = new Tuning(); t.Update(Temperament.Equal, 0, 0);
        for (int n = 0; n < 128; n++) Assert.Equal((float)n, t.MidiFor(n));      // bit-identical to the old integer note
    }

    [Fact]
    public void JustMajor_InC_AndInD()
    {
        double c4 = C4(Temperament.PureMajor, 0);
        Assert.Equal(261.6255653, c4, 4);                                         // tonic stays 12-TET
        Assert.Equal(c4 * 5 / 4, Hz(Temperament.PureMajor, 0, 64), 3);            // E4
        Assert.Equal(c4 * 3 / 2, Hz(Temperament.PureMajor, 0, 67), 3);            // G4
        Assert.Equal(c4 * 9 / 8, Hz(Temperament.PureMajor, 0, 62), 3);
        Assert.Equal(c4 * 2, Hz(Temperament.PureMajor, 0, 72), 3);                // octave pure
        double d4 = Hz(Temperament.PureMajor, 2, 62);
        Assert.Equal(293.6647679, d4, 3);                                         // D tonic at 12-TET pitch
        Assert.Equal(d4 * 5 / 4, Hz(Temperament.PureMajor, 2, 66), 3);            // F#4
        Assert.Equal(d4 * 3 / 2, Hz(Temperament.PureMajor, 2, 69), 3);            // A4 = fifth above D
        Assert.Equal(d4 * 15 / 8 / 2, Hz(Temperament.PureMajor, 2, 61), 3);       // C#4 = major seventh below D5
    }

    [Fact]
    public void JustMinor_Table_DiffersFromMajor()
    {
        double c4 = C4(Temperament.PureMinor, 0);
        Assert.Equal(c4 * 6 / 5, Hz(Temperament.PureMinor, 0, 63), 3);   // Eb
        Assert.Equal(c4 * 10 / 9, Hz(Temperament.PureMinor, 0, 62), 3);  // D
        Assert.Equal(c4 * 16 / 9, Hz(Temperament.PureMinor, 0, 70), 3);  // Bb
        Assert.Equal(c4 * 8 / 5, Hz(Temperament.PureMinor, 0, 68), 3);   // Ab
        Assert.NotEqual(Hz(Temperament.PureMajor, 0, 62), Hz(Temperament.PureMinor, 0, 62), 3);
        Assert.Equal(12, Tuning.JustMinor.Length);
    }

    [Fact]
    public void Pythagorean_SharpAndFlat_DifferOnEnharmonics()
    {
        double c4 = C4(Temperament.PythagoreanSharp, 0);
        Assert.Equal(c4 * 3 / 2, Hz(Temperament.PythagoreanSharp, 0, 67), 3);
        Assert.Equal(c4 * 9 / 8, Hz(Temperament.PythagoreanSharp, 0, 62), 3);
        Assert.Equal(c4 * 81 / 64, Hz(Temperament.PythagoreanSharp, 0, 64), 3);
        Assert.Equal(c4 * 729 / 512, Hz(Temperament.PythagoreanSharp, 0, 66), 2);   // F#
        Assert.Equal(c4 * 1024 / 729, Hz(Temperament.PythagoreanFlat, 0, 66), 2);   // Gb
        Assert.Equal(c4 * 2187 / 2048, Hz(Temperament.PythagoreanSharp, 0, 61), 2); // C#
        Assert.Equal(c4 * 256 / 243, Hz(Temperament.PythagoreanFlat, 0, 61), 2);    // Db
        Assert.Equal(c4 * 4 / 3, Hz(Temperament.PythagoreanFlat, 0, 65), 3);
        // pythagorean comma between the two spellings of the tritone: 23.46 cents
        Assert.Equal(23.46, Cents(Hz(Temperament.PythagoreanSharp, 0, 66), Hz(Temperament.PythagoreanFlat, 0, 66)), 1);
    }

    [Fact]
    public void SpecialTemperaments_ConcertPitchOnC4_Then12Tet()
    {
        Assert.Equal(528.0, C4(Temperament.Solfeggio, 4), 3);
        Assert.Equal(528.0 * Math.Pow(2, 2 / 12.0), Hz(Temperament.Solfeggio, 4, 62), 2);
        Assert.Equal(528.0 / 2, Hz(Temperament.Solfeggio, 4, 48), 4);
        Assert.Equal(174.0, C4(Temperament.Solfeggio, 0), 3);
        Assert.Equal(963.0, C4(Temperament.Solfeggio, 8), 3);
        Assert.Equal(341.0, C4(Temperament.Chakra, 3), 3);
        Assert.Equal(256.0, C4(Temperament.Chakra, 0), 3);
        Assert.Equal(194.18, C4(Temperament.Planetary, 0), 3);
        Assert.Equal(352.0, C4(Temperament.Planetary, 6), 3);
        Assert.Equal(480.0 * Math.Pow(2, 7 / 12.0), Hz(Temperament.Chakra, 6, 67), 2);
    }

    [Fact]
    public void SpecialTemperaments_IgnoreMasterTune()
    {
        foreach (var t in new[] { Temperament.Solfeggio, Temperament.Chakra, Temperament.Planetary })
            Assert.Equal(C4(t, 0, 0), C4(t, 0, 75), 9);
        Assert.NotEqual(C4(Temperament.PureMajor, 0, 0), C4(Temperament.PureMajor, 0, 40), 3);
    }

    [Fact]
    public void Keys_AreClamped_PerTemperament()
    {
        Assert.Equal(C4(Temperament.Chakra, 6), C4(Temperament.Chakra, 9));
        Assert.Equal(C4(Temperament.Solfeggio, 8), C4(Temperament.Solfeggio, 11));
        Assert.Equal(C4(Temperament.Planetary, 6), C4(Temperament.Planetary, 99));
        Assert.Equal(C4(Temperament.PureMajor, 0), C4(Temperament.PureMajor, -3));
        Assert.Equal(7, Tuning.KeyCount(Temperament.Chakra));
        Assert.Equal(9, Tuning.KeyCount(Temperament.Solfeggio));
        Assert.Equal(12, Tuning.KeyCount(Temperament.PythagoreanFlat));
    }

    [Fact]
    public void NoiseTable_KeepsSemitoneSteps()
    {
        var tu = new Tuning(semitoneOnly: true);
        foreach (var t in new[] { Temperament.Equal, Temperament.PureMajor, Temperament.PureMinor, Temperament.PythagoreanSharp, Temperament.PythagoreanFlat })
        {
            tu.Update(t, 3, 50);
            for (int n = 0; n < 128; n++) Assert.Equal((float)n, tu.MidiFor(n));
        }
        tu.Update(Temperament.Solfeggio, 4, 0);
        double off = Cents(528.0, 261.6255653);
        Assert.Equal(off / 100.0, tu.MidiFor(60) - 60, 4);
        Assert.Equal(1.0, tu.MidiFor(62) - tu.MidiFor(61), 4);
    }

    // ------------------------------------------------------------------ rendered pitch

    private static ParameterStore Patch(int layer = 0)
    {
        var p = new ParameterStore();
        p.Set(layer, ParamId.Wave, 3);
        p.Set(layer, ParamId.Harmonic, 0);
        p.Set(layer, ParamId.Attack, 0);
        p.Set(layer, ParamId.FilterType, 0);
        return p;
    }

    /// <summary>
    /// Renders audio and finds the spectral peak within +-1 semitone of <paramref name="expected"/> (Hann-windowed DFT,
    /// coarse 1-cent scan then bisection-style refinement). The SIN.S wave carries small non-harmonic content, so zero crossings are unreliable.
    /// </summary>
    private static double Measure(SynthLayer l, double expected, double seconds = 1.5, double skip = 0.4)
    {
        var buf = new float[1024];
        int total = (int)(seconds * Fs), skipN = (int)(skip * Fs);
        var x = new List<float>();
        for (int pos = 0; pos < total; pos += 512)
        {
            l.Render(buf, 512);
            for (int i = 0; i < 512; i++) if (pos + i >= skipN) x.Add(buf[2 * i]);
        }
        var xs = x.ToArray();
        int n = xs.Length;
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = xs[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        double Mag(double f)
        {
            double re = 0, im = 0, k = 2 * Math.PI * f / Fs;
            for (int i = 0; i < n; i += 2) { double a = k * i; re += w[i] * Math.Cos(a); im += w[i] * Math.Sin(a); }
            return re * re + im * im;
        }
        double best = -100, bm = -1;
        for (double c = -100; c <= 100; c += 2) { double m = Mag(expected * Math.Pow(2, c / 1200)); if (m > bm) { bm = m; best = c; } }
        double lo = best - 2, hi = best + 2;
        for (int it = 0; it < 25; it++)       // golden-ish ternary search on the unimodal peak
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (Mag(expected * Math.Pow(2, m1 / 1200)) < Mag(expected * Math.Pow(2, m2 / 1200))) lo = m1; else hi = m2;
        }
        return expected * Math.Pow(2, (lo + hi) / 2 / 1200);
    }

    private static void AssertHz(SynthLayer l, double expected)
    {
        double got = Measure(l, expected);
        Assert.True(Math.Abs(Cents(got, expected)) < 0.5, $"expected {expected} Hz, measured {got} Hz ({Cents(got, expected):F3} cents)");
    }

    private static SynthLayer Play(ParameterStore p, int note, int layer = 0)
    {
        var l = new SynthLayer(layer, Fs, p, Bank);
        l.NoteOn(note, 127);
        return l;
    }

    [Fact]
    public void Rendered_Equal_A4_440_AndMasterTune()
    {
        var p = Patch();
        AssertHz(Play(p, 69), 440.0);
        p.Set(GlobalParamId.MasterTuneCents, 75);
        AssertHz(Play(p, 69), 440.0 * Math.Pow(2, 75 / 1200.0));
        p.Set(GlobalParamId.MasterTuneCents, -75);
        AssertHz(Play(p, 69), 440.0 * Math.Pow(2, -75 / 1200.0));
    }

    [Fact]
    public void Rendered_JustMajor_E4_IsFiveFourthsOfC4()
    {
        var p = Patch();
        p.Set(GlobalParamId.Temperament, (float)Temperament.PureMajor);
        const double c4 = 261.6255653005986;
        AssertHz(Play(p, 60), c4);
        AssertHz(Play(p, 64), c4 * 5 / 4);
        p.Set(0, ParamId.TuningKey, 2);   // D
        const double d4 = 293.6647679174076;
        AssertHz(Play(p, 62), d4);
        AssertHz(Play(p, 66), d4 * 5 / 4);
    }

    [Fact]
    public void Rendered_Special_Solfeggio528_IgnoresMasterAndOctave_TuneAllShifts()
    {
        var p = Patch();
        p.Set(GlobalParamId.Temperament, (float)Temperament.Solfeggio);
        p.Set(0, ParamId.TuningKey, 4);
        p.Set(GlobalParamId.MasterTuneCents, 60);
        p.Set(0, ParamId.Octave, 2);
        AssertHz(Play(p, 60), 528.0);
        Assert.Equal(2f, p.Get(0, ParamId.Octave));                       // stored param untouched
        AssertHz(Play(p, 62), 528.0 * Math.Pow(2, 2 / 12.0));
        p.Set(GlobalParamId.TuneAllCents, -1200);                         // applies to special temperaments too
        AssertHz(Play(p, 60), 264.0);
    }

    [Fact]
    public void Rendered_TuneAll_PlusMinusOctave_AndOctaveStillWorksForNormalTemperaments()
    {
        var p = Patch();
        p.Set(GlobalParamId.TuneAllCents, 1200);
        AssertHz(Play(p, 69), 880.0);
        p.Set(GlobalParamId.TuneAllCents, -1200);
        AssertHz(Play(p, 69), 220.0);
        p.Set(GlobalParamId.TuneAllCents, 0);
        p.Set(0, ParamId.Octave, 1);
        AssertHz(Play(p, 69), 880.0);
    }

    [Fact]
    public void Rendered_TuneAll_IsSmoothed_NoStep()
    {
        var p = Patch();
        var l = Play(p, 69);
        var buf = new float[1024];
        for (int i = 0; i < 100; i++) l.Render(buf, 512);
        p.Set(GlobalParamId.TuneAllCents, 1200);
        l.Render(buf, 32);
        Assert.True(Math.Abs(buf[0]) < 1.5f && float.IsFinite(buf[63]));
        for (int i = 0; i < 200; i++) l.Render(buf, 512);
        Assert.True(Math.Abs(Cents(Measure(l, 880.0), 880.0)) < 1.0);   // settled
    }

    [Fact]
    public void PerLayer_DifferentKeys()
    {
        var p = Patch(0); p.Set(1, ParamId.Wave, 3); p.Set(1, ParamId.Harmonic, 0); p.Set(1, ParamId.Attack, 0); p.Set(1, ParamId.FilterType, 0);
        p.Set(GlobalParamId.Temperament, (float)Temperament.Chakra);
        p.Set(0, ParamId.TuningKey, 0);   // 256
        p.Set(1, ParamId.TuningKey, 6);   // 480
        AssertHz(Play(p, 60, 0), 256.0);
        AssertHz(Play(p, 60, 1), 480.0);
    }


    [Fact]
    public void Glide_GoesBetweenTunedPitches_AndEndsExactlyOnTable()
    {
        var p = new ParameterStore();
        var tu = new Tuning();
        tu.Update(Temperament.PythagoreanSharp, 0, 0);
        var v = new SynthVoice(Fs, 1) { Tuning = tu, GlideMs = 50 };
        v.SetTimes(0, 200);
        v.Start(Bank.Get(3), 60, 127, 1);
        v.SlideTo(66);                                                   // F# in Pythagorean sharp: 729/512, not 12-TET
        var mod = new LayerModulation(0, Fs, p);
        var l = new float[Fs / 2]; var r = new float[Fs / 2];
        for (int o = 0; o < l.Length; o += 32)
        {
            mod.Update(); mod.BeginSub(32);
            var b = new VoiceBlock { Harmonic0 = 0, Filter = Sambient.Dsp.Filters.FilterMode.Off, CutoffKnob = 127, Mod = mod };
            v.Process(l.AsSpan(o, 32), r.AsSpan(o, 32), 32, in b);
        }
        Assert.Equal(tu.MidiFor(66), v.SoundingNote);
        Assert.NotEqual(66f, v.SoundingNote);
    }

    [Fact]
    public void Noise_SpecialTemperament_ShiftsRoot_NormalOnesDoNot()
    {
        var p = new ParameterStore();
        var nb = new NoiseBank();
        var nl = new NoiseLayer(ParameterStore.NoiseLayerIndex, Fs, p, nb);
        int li = ParameterStore.NoiseLayerIndex;
        p.Set(GlobalParamId.Temperament, (float)Temperament.PureMajor);
        p.Set(GlobalParamId.MasterTuneCents, 40);
        nl.NoteOn(64, 100);
        var buf = new float[512];
        nl.Render(buf, 256);
        var v = nl.VoicesForTest.First(x => x.Active);
        Assert.Equal(64f, v.SoundingNote);                      // 12-TET semitone steps, no just ratio, no master tune
        p.Set(GlobalParamId.Temperament, (float)Temperament.Solfeggio);
        p.Set(li, ParamId.TuningKey, 4);
        var nl2 = new NoiseLayer(li, Fs, p, nb);
        nl2.NoteOn(62, 100);
        nl2.Render(buf, 256);
        var v2 = nl2.VoicesForTest.First(x => x.Active);
        Assert.Equal(528.0, 440.0 * Math.Pow(2, (v2.SoundingNote - 2 - 69) / 12.0), 3);
    }

    [Fact]
    public void Render_AfterTemperamentChange_DoesNotAllocate()
    {
        var p = Patch();
        var l = Play(p, 60);
        var n = new NoiseLayer(ParameterStore.NoiseLayerIndex, Fs, p, new NoiseBank());
        var buf = new float[1024];
        for (int i = 0; i < 20; i++) { l.Render(buf, 512); n.Render(buf, 512); }
        for (int t = 0; t < 8; t++) { p.Set(GlobalParamId.Temperament, t); l.Render(buf, 512); n.Render(buf, 512); }   // warm-up: first-use JIT paths
        long total = 0;
        for (int i = 0; i < 40; i++)
        {
            p.Set(GlobalParamId.Temperament, (float)(i % 8));     // parameter writes happen on the control thread: not measured
            p.Set(0, ParamId.TuningKey, i % 12);
            p.Set(GlobalParamId.MasterTuneCents, i - 20);
            p.Set(GlobalParamId.TuneAllCents, i * 10);
            long before = GC.GetAllocatedBytesForCurrentThread();
            l.Render(buf, 512); n.Render(buf, 512);
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, total);
    }
}
