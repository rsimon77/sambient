using Sambient.Dsp.Filters;
using Sambient.Dsp.Modulation;
using Sambient.Dsp.Params;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;
using Xunit.Abstractions;

namespace Sambient.Tests;

/// <summary>M3: LFO shapes / triggers, BlendWave structures, MOD, LFO1/LFO2 destinations, voice-mode contract of the voices.</summary>
public class StructureModTests
{
    private const int Fs = 48000;
    private readonly ITestOutputHelper _out;
    public StructureModTests(ITestOutputHelper output) => _out = output;

    private static readonly WavetableBank SharedBank = MakeBank();

    private static WavetableBank MakeBank()
    {
        var b = new WavetableBank();
        foreach (int w in new[] { 1, 3, 8, 25 }) b.EnsureBuiltAsync(w).GetAwaiter().GetResult();
        return b;
    }

    // =========================================================================== LFO

    private static float[] LfoRun(LfoWave w, float hz, int samples, int trig = Lfo.TrigOff, uint seed = 7)
    {
        var l = new Lfo(Fs, seed);
        l.Configure(w, hz, trig);
        l.Retrigger();
        var x = new float[samples];
        l.Fill(x);
        return x;
    }

    [Fact]
    public void Lfo_WaveEnum_MatchesEngineLfoShape()
    {
        var names = Enum.GetNames<LfoShape>();
        Assert.Equal(24, names.Length);
        Assert.Equal(names, Enum.GetNames<LfoWave>());
    }

    [Fact]
    public void Lfo_AllShapes_AreBoundedAndFinite()
    {
        foreach (var w in Enum.GetValues<LfoWave>())
            foreach (float hz in new[] { 2f, 7f, 400f })
            {
                var x = LfoRun(w, hz, Fs * 8);
                foreach (float v in x)
                {
                    Assert.True(float.IsFinite(v), $"{w}@{hz}");
                    Assert.InRange(v, -1.0001f, 1.0001f);
                }
                Assert.True(x.Max() - x.Min() > 0.5f, $"{w}@{hz} does not move");
            }
    }

    [Fact]
    public void Lfo_DeterministicShapes_HaveTheConfiguredPeriod()
    {
        const float hz = 2f;
        int period = (int)(Fs / hz);
        foreach (var w in new[]
        {
            LfoWave.Sine, LfoWave.Square, LfoWave.Triangle, LfoWave.Saw, LfoWave.ReverseSaw, LfoWave.Log, LfoWave.ReverseLog,
            LfoWave.Pulse10, LfoWave.Pulse25, LfoWave.Pulse75, LfoWave.Pulse90, LfoWave.Step2, LfoWave.Step3, LfoWave.Step4,
            LfoWave.Step5, LfoWave.Step6, LfoWave.Step7, LfoWave.RampUp, LfoWave.RampDown,
        })
        {
            var x = LfoRun(w, hz, period * 4);
            int off = 0;     // an edge may land one sample earlier / later in different cycles (double phase accumulation)
            for (int i = 0; i < period; i++)
                if (MathF.Abs(x[2 * period + i] - x[3 * period + i]) > 0.03f) off++;
            Assert.True(off <= 6, $"{w}: {off} samples differ between cycles");
        }
        // sine crosses zero twice per cycle
        var s = LfoRun(LfoWave.Sine, hz, period * 4);
        int zc = 0;
        for (int i = 1; i < s.Length; i++) if (s[i - 1] < 0 != s[i] < 0) zc++;
        Assert.InRange(zc, 7, 9);
    }

    [Fact]
    public void Lfo_LowVariants_RunEightTimesSlower()
    {
        foreach (var (low, normal) in new[]
        {
            (LfoWave.LowSine, LfoWave.Sine), (LfoWave.LowTriangle, LfoWave.Triangle), (LfoWave.LowSmoothRandom, LfoWave.SmoothRandom),
        })
        {
            var a = LfoRun(low, 8f, Fs);
            var b = LfoRun(normal, 1f, Fs);
            for (int i = 0; i < a.Length; i++) Assert.True(MathF.Abs(a[i] - b[i]) < 1e-4f, $"{low} sample {i}");
        }
    }

    [Fact]
    public void Lfo_Random_IsSeededDeterministic_AndHoldsPerCycle()
    {
        var a = LfoRun(LfoWave.Random, 4f, Fs, seed: 5);
        var b = LfoRun(LfoWave.Random, 4f, Fs, seed: 5);
        var c = LfoRun(LfoWave.Random, 4f, Fs, seed: 6);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        // sample & hold: within one cycle the (slewed) value settles on a plateau
        int period = Fs / 4;
        Assert.True(MathF.Abs(a[period - 5] - a[period - 50]) < 1e-3f);
    }

    [Fact]
    public void Lfo_DiscontinuousShapes_AreSlewedNotStepped()
    {
        foreach (var w in new[] { LfoWave.Square, LfoWave.Random, LfoWave.Pulse25, LfoWave.Step5, LfoWave.Saw, LfoWave.RampUp })
        {
            var x = LfoRun(w, 3f, Fs * 2);
            float maxStep = 0;
            for (int i = 1; i < x.Length; i++) maxStep = Math.Max(maxStep, MathF.Abs(x[i] - x[i - 1]));
            Assert.True(maxStep < 0.1f, $"{w} steps by {maxStep}");     // a hard edge would be up to 2.0
        }
    }

    [Fact]
    public void Lfo_Trigger_Off_IsFreeRunning_Inf_Resets_N_RunsCyclesThenHolds()
    {
        var off = new Lfo(Fs, 1); off.Configure(LfoWave.Sine, 3f, Lfo.TrigOff);
        for (int i = 0; i < 12345; i++) off.Next();
        double ph = off.Phase;
        off.Retrigger();
        Assert.Equal(ph, off.Phase);                       // free-running: a note-on does not touch the phase

        var inf = new Lfo(Fs, 1); inf.Configure(LfoWave.Sine, 3f, Lfo.TrigInf);
        for (int i = 0; i < 12345; i++) inf.Next();
        Assert.True(inf.Phase > 0);
        inf.Retrigger();
        Assert.Equal(0.0, inf.Phase);

        var n = new Lfo(Fs, 1); n.Configure(LfoWave.RampUp, 10f, 2);     // 2 cycles of 4800 samples
        n.Retrigger();
        for (int i = 0; i < 9000; i++) n.Next();
        Assert.False(n.IsHolding);
        for (int i = 0; i < 1500; i++) n.Next();
        Assert.True(n.IsHolding);
        Assert.Equal(2, n.CompletedCycles);
        float held = n.Next();
        Assert.InRange(held, 0.98f, 1.0001f);              // ramp ends at +1 and stays
        for (int i = 0; i < 20000; i++) Assert.Equal(held, n.Next(), 4);
        n.Retrigger();
        Assert.False(n.IsHolding);
    }

    [Fact]
    public void Curves_LfoRate_Detune_Balance_Pitch_Ratio()
    {
        Assert.Equal(0.02f, ParamCurves.LfoRateHz(0), 4);
        Assert.Equal(30f, ParamCurves.LfoRateHz(127), 2);
        Assert.InRange(ParamCurves.LfoRateHz(64), 0.6f, 1.2f);
        for (int v = 1; v <= 127; v++) Assert.True(ParamCurves.LfoRateHz(v) > ParamCurves.LfoRateHz(v - 1));
        Assert.Equal(2000f, ParamCurves.ModRateHz(127, ringMod: true), 1);
        Assert.Equal(30f, ParamCurves.ModRateHz(127, ringMod: false), 2);

        Assert.Equal(0f, ParamCurves.DetuneCents(63.5f), 4);
        Assert.Equal(50f, ParamCurves.DetuneCents(127), 3);
        Assert.Equal(-50f, ParamCurves.DetuneCents(0), 3);
        Assert.InRange(ParamCurves.DetuneCents(63.5f + 6.35f), 0.4f, 0.6f);     // 10 % of the travel ~ half a cent

        var (a0, b0) = ParamCurves.BalanceGains(0);
        var (a1, b1) = ParamCurves.BalanceGains(127);
        var (ac, bc) = ParamCurves.BalanceGains(63.5f);
        Assert.Equal(1f, a0, 4); Assert.Equal(0f, b0, 4);
        Assert.Equal(0f, a1, 4); Assert.Equal(1f, b1, 4);
        Assert.Equal(ac, bc, 4); Assert.Equal(1f, ac * ac + bc * bc, 4);        // equal power

        Assert.Equal(12f, ParamCurves.PitchSemitones(240), 5);
        Assert.Equal(0.05f, ParamCurves.PitchSemitones(1), 5);                   // 5 cents
        Assert.Equal(0.5f, ParamCurves.Atmos2Ratio(-240), 4);
        Assert.Equal(1f, ParamCurves.Atmos2Ratio(0), 5);
        Assert.Equal(15.99f, ParamCurves.Atmos2Ratio(240), 3);
        foreach (int p in new[] { -240, -100, 0, 37, 240 })
            Assert.Equal(p, ParamCurves.Atmos2RatioToPitch(ParamCurves.Atmos2Ratio(p)));
    }

    // =========================================================================== voice-level harness (no layer needed)

    private sealed class VoiceRig
    {
        public readonly ParameterStore P = new();
        public readonly LayerModulation Mod;
        public readonly SynthVoice Voice;
        public readonly int Layer;
        public float Cutoff = 127, Reso = 0, Pan = 0, Harmonic = 0;
        public FilterMode Filter = FilterMode.Off;
        public int Wave = 3;

        public VoiceRig(int layer = 0)
        {
            Layer = layer;
            P.Set(layer, ParamId.Wave, 3);
            Mod = new LayerModulation(layer, Fs, P);
            Voice = new SynthVoice(Fs, 1);
            Voice.SetTimes(0, 200);
        }

        public void Set(ParamId id, float v) { P.Set(Layer, id, v); }

        public void Start(int note = 69, int vel = 127) => Voice.Start(SharedBank.Get(Wave), note, vel, 1);

        public (float[] l, float[] r) Run(int frames)
        {
            var l = new float[frames]; var r = new float[frames];
            const int sub = 32;
            for (int o = 0; o < frames; o += sub)
            {
                int n = Math.Min(sub, frames - o);
                Mod.Update();
                Mod.BeginSub(n);
                var b = new VoiceBlock
                {
                    Harmonic0 = Harmonic, HarmonicStep = 0, PitchSemis = 0, Filter = Filter,
                    CutoffKnob = Cutoff, ResoKnob = Reso, Pan = Pan, Mod = Mod,
                };
                Voice.Process(l.AsSpan(o, n), r.AsSpan(o, n), n, in b);
            }
            return (l, r);
        }
    }

    private static double Goertzel(float[] x, double hz, int start, int len)
    {
        double w = 2 * Math.PI * hz / Fs, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = 0; i < len; i++)
        {
            double s = x[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / len)) + c * s1 - s2;
            s2 = s1; s1 = s;
        }
        return (s1 * s1 + s2 * s2 - c * s1 * s2) / len;
    }

    private static int ZeroCrossings(float[] x, int start, int len)
    {
        int z = 0;
        for (int i = start + 1; i < start + len; i++) if (x[i - 1] < 0 != x[i] < 0) z++;
        return z;
    }

    private static double Rms(float[] x, int start, int len)
    {
        double s = 0;
        for (int i = 0; i < len; i++) s += (double)x[start + i] * x[start + i];
        return Math.Sqrt(s / len);
    }

    [Fact]
    public void Voice_SlideTo_GlidesInLogDomain_WithoutRetrigger()
    {
        var rig = new VoiceRig();
        rig.Set(ParamId.Structure, (float)Structure.Atmos1);
        rig.Voice.SetTimes(0, 200);
        rig.Start(60);
        rig.Run(2048);
        rig.Voice.GlideMs = 400;
        rig.Voice.SlideTo(72);
        Assert.Equal(72, rig.Voice.Note);
        Assert.Equal(60f, rig.Voice.SoundingNote, 3);

        var (l, _) = rig.Run((int)(0.2 * Fs));                 // half of the glide time
        float mid = rig.Voice.SoundingNote;
        Assert.InRange(mid, 62f, 71f);                        // moving, not there yet
        rig.Run((int)(0.6 * Fs));                              // 1 s after the slide started: > 95 % reached
        Assert.InRange(rig.Voice.SoundingNote, 71.5f, 72.0001f);
        // exponential approach: the first half of the glide covers more than half of the interval
        Assert.True(mid - 60f > 6f);
        // the envelope kept holding: still full-level sound after the glide (a retrigger/restart would not change that, a release would)
        var (l2, _) = rig.Run(4800);
        Assert.True(Rms(l2, 0, l2.Length) > 0.1);
        Assert.True(rig.Voice.Gate);

        // GlideMs = 0: instant
        rig.Voice.GlideMs = 0;
        rig.Voice.SlideTo(55);
        Assert.Equal(55f, rig.Voice.SoundingNote, 3);
    }

    [Fact]
    public void Voice_SlideTo_PitchFollowsTheGlide()
    {
        var rig = new VoiceRig();
        rig.Set(ParamId.Structure, (float)Structure.Atmos1);
        rig.Start(57);                                          // 220 Hz
        var (a, _) = rig.Run(Fs);
        int z0 = ZeroCrossings(a, Fs / 2, Fs / 2);              // crossings in 0.5 s ~ 220 per ... 2 per cycle
        rig.Voice.GlideMs = 100;
        rig.Voice.SlideTo(69);                                  // 440 Hz
        rig.Run(Fs / 2);
        var (b, _) = rig.Run(Fs);
        int z1 = ZeroCrossings(b, Fs / 2, Fs / 2);
        Assert.InRange((double)z1 / z0, 1.9, 2.1);
    }

    [Fact]
    public void Voice_DetuneOffsetCents_ShiftsFrequency()
    {
        int Crossings(float cents)
        {
            var rig = new VoiceRig();
            rig.Set(ParamId.Structure, (float)Structure.Atmos1);
            rig.Voice.DetuneOffsetCents = cents;
            rig.Start(45);                                      // 110 Hz
            var (l, _) = rig.Run(Fs * 2);
            return ZeroCrossings(l, Fs / 2, Fs);
        }
        double ratio = (double)Crossings(100f) / Crossings(0f);
        Assert.InRange(ratio, 1.05, 1.07);                       // +100 cents = x1.0595
        Assert.InRange((double)Crossings(-1200f) / Crossings(0f), 0.49, 0.51);
    }

    [Fact]
    public void Voice_PanSpread_MovesTheVoiceInTheStereoField()
    {
        float Balance(float spread)
        {
            var rig = new VoiceRig();
            rig.Set(ParamId.Structure, (float)Structure.Atmos1);
            rig.Voice.PanSpread = spread;
            rig.Start(57);
            var (l, r) = rig.Run(Fs / 2);
            return (float)(Rms(r, 4800, 19200) / Math.Max(Rms(l, 4800, 19200), 1e-9));
        }
        Assert.InRange(Balance(0f), 0.98f, 1.02f);
        Assert.True(Balance(1f) > 50f);
        Assert.True(Balance(-1f) < 0.02f);
        Assert.InRange(Balance(0.5f), 1.5f, 6f);
    }

    [Fact]
    public void Voice_Mix_IsEqualPowerCentred_LikeTheOldLayerPan()
    {
        var rig = new VoiceRig();
        rig.Set(ParamId.Structure, (float)Structure.Atmos1);
        rig.Start(57);
        var (l, r) = rig.Run(Fs / 2);
        double rms = Rms(l, 4800, 9600);
        Assert.InRange(rms, 0.3, 0.8);                          // sine: ~0.707 * 0.707 * velocity-gain 1.0
        Assert.Equal(rms, Rms(r, 4800, 9600), 3);
    }

    [Fact]
    public void Voice_Mod_Atmos1_RingModulation_CreatesSidebands()
    {
        double Side(float depth)
        {
            var rig = new VoiceRig();
            rig.Set(ParamId.Structure, (float)Structure.Atmos1);
            rig.Set(ParamId.ModShape, (float)LfoShape.Sine);
            rig.Set(ParamId.ModRate, 100);
            rig.Set(ParamId.ModDepth, depth);
            rig.Start(69);                                      // 440 Hz carrier
            float fm = ParamCurves.ModRateHz(100, true);
            var (l, _) = rig.Run(Fs);
            double side = Goertzel(l, 440 + fm, 4800, 32768) + Goertzel(l, 440 - fm, 4800, 32768);
            double car = Goertzel(l, 440, 4800, 32768);
            return side / car;
        }
        Assert.True(ParamCurves.ModRateHz(100, true) > 100f);    // audio-ish range
        Assert.True(Side(127) > 0.1);
        Assert.True(Side(127) > 100 * Side(0) + 1e-6);
    }

    [Fact]
    public void Voice_Atmos2_FmAmount_IncreasesBandwidth_AndRatioIsPitch()
    {
        double Brightness(float amount, float pitch, float fb = 0)
        {
            var rig = new VoiceRig();
            rig.Set(ParamId.Structure, (float)Structure.Atmos2);
            rig.Set(ParamId.Balance, amount);
            rig.Set(ParamId.Pitch, pitch);
            rig.Set(ParamId.Detune, fb);
            rig.Start(57);
            var (l, _) = rig.Run(Fs);
            double d = 0, e = 0;
            for (int i = 9600; i < l.Length; i++) { float dx = l[i] - l[i - 1]; d += dx * dx; e += l[i] * l[i]; }
            return d / e;
        }
        double b0 = Brightness(0, 0), b1 = Brightness(60, 0), b2 = Brightness(127, 0);
        Assert.True(b1 > 1.5 * b0, $"{b0} {b1}");
        Assert.True(b2 > 1.5 * b1, $"{b1} {b2}");
        Assert.True(Brightness(100, 0, fb: 127) != Brightness(100, 0, fb: 0));     // feedback changes the sound
        Assert.True(Brightness(100, 200) > Brightness(100, -200));                 // a higher ratio is brighter

        // amount 0: the carrier alone
        var rig0 = new VoiceRig();
        rig0.Set(ParamId.Structure, (float)Structure.Atmos2);
        rig0.Set(ParamId.Balance, 0);
        rig0.Start(57);
        var (c, _) = rig0.Run(Fs);
        Assert.InRange(ZeroCrossings(c, 9600, Fs / 2), 215, 225);                    // 220 Hz sine, 2 crossings per cycle per ... 110 cycles/0.5 s
    }

    [Fact]
    public void Voice_EveryStructure_IsFinite_AtExtremeParams()
    {
        var rnd = new Random(3);
        foreach (var st in Enum.GetValues<Structure>())
            foreach (int extreme in new[] { 0, 1 })
            {
                var rig = new VoiceRig();
                rig.Set(ParamId.Structure, (float)st);
                rig.Set(ParamId.Pitch, extreme == 0 ? -240 : 240);
                rig.Set(ParamId.Detune, extreme == 0 ? 0 : 127);
                rig.Set(ParamId.Balance, extreme == 0 ? 0 : 127);
                rig.Set(ParamId.ModDepth, 127);
                rig.Set(ParamId.ModRate, extreme == 0 ? 0 : 127);
                rig.Set(ParamId.ModShape, (float)LfoShape.Random);
                rig.Set(ParamId.Lfo1Assign, (float)LfoAssign.Harmonic); rig.Set(ParamId.Lfo1Depth, 127); rig.Set(ParamId.Lfo1Rate, 127);
                rig.Set(ParamId.Lfo2Assign, (float)LfoAssign.Pitch); rig.Set(ParamId.Lfo2Depth, 127); rig.Set(ParamId.Lfo2Rate, 127);
                rig.Harmonic = rnd.Next(128);
                rig.Start(extreme == 0 ? 0 : 127);
                var (l, r) = rig.Run(Fs / 2);
                foreach (float v in l.Concat(r)) Assert.True(float.IsFinite(v) && MathF.Abs(v) < 20f, $"{st} {extreme}");
            }
    }

    [Fact]
    public void Voice_StructureChange_WhileSounding_IsClickFree()
    {
        var rig = new VoiceRig();
        rig.Set(ParamId.Structure, (float)Structure.Drone1);
        rig.Set(ParamId.Pitch, 140);
        rig.Start(57);
        var (a, _) = rig.Run(Fs / 2);
        float baseline = 0;
        for (int i = 4800; i < a.Length; i++) baseline = Math.Max(baseline, MathF.Abs(a[i] - a[i - 1]));
        rig.Set(ParamId.Structure, (float)Structure.Pad1);
        var (b, _) = rig.Run(Fs / 2);
        float jump = 0;
        for (int i = 1; i < b.Length; i++) jump = Math.Max(jump, MathF.Abs(b[i] - b[i - 1]));
        Assert.True(jump < baseline * 2f, $"jump {jump} vs steady slope {baseline}");
        Assert.Equal(Structure.Pad1, rig.Voice.CurrentStructure);           // switched after the dip
    }

    [Fact]
    public void Voice_Balance_Extremes_Isolate_Oscillators()
    {
        // DRONE1: osc2 is +7 semitones (3:2). Balance 0 -> only osc1, 127 -> only osc2.
        double Ratio(float bal)
        {
            var rig = new VoiceRig();
            rig.Set(ParamId.Structure, (float)Structure.Drone1);
            rig.Set(ParamId.Pitch, 140);
            rig.Set(ParamId.Balance, bal);
            rig.Start(57);
            var (l, _) = rig.Run(Fs);
            double f1 = 220.0, f2 = 220.0 * Math.Pow(2, 7 / 12.0);
            return Goertzel(l, f2, 9600, 32768) / Goertzel(l, f1, 9600, 32768);
        }
        Assert.True(Ratio(0) < 0.001, $"{Ratio(0)}");
        Assert.True(Ratio(127) > 1000, $"{Ratio(127)}");
        Assert.InRange(Ratio(63.5f), 0.3, 3);

        var rigC = new VoiceRig();
        rigC.Set(ParamId.Structure, (float)Structure.Drone1);
        rigC.Set(ParamId.Balance, 0);
        rigC.Start(57); rigC.Run(512);
        Assert.Equal(1, rigC.Mod.OscillatorsRendered);                       // the silent oscillator is skipped
    }

    [Fact]
    public void Voice_Structures_RenderTheExpectedOscillators()
    {
        foreach (var (st, n) in new[]
        {
            (Structure.Drone1, 2), (Structure.Drone2, 2), (Structure.Pad1, 3), (Structure.Pad2, 3), (Structure.Atmos1, 1), (Structure.Atmos2, 2),
        })
        {
            Assert.Equal(n, StructureInfo.OscillatorCount(st));
            var rig = new VoiceRig();
            rig.Set(ParamId.Structure, (float)st);
            rig.Start(57);
            var (l, _) = rig.Run(2048);
            Assert.Equal(n, rig.Mod.OscillatorsRendered);
            Assert.True(Rms(l, 1024, 1024) > 0.05, st.ToString());
        }
        Assert.Equal(3, StructureInfo.OscillatorCost(Structure.Atmos1));
        Assert.True(StructureInfo.IsDrone(Structure.Drone2));
        Assert.False(StructureInfo.IsDrone(Structure.Pad1));
    }

    [Fact]
    public void Voice_Drone1_DetuneProducesSlowBeating()
    {
        var rig = new VoiceRig();
        rig.Set(ParamId.Structure, (float)Structure.Drone1);
        rig.Set(ParamId.Detune, 63.5f + 20f);                  // ~ 2.5 cents -> ~0.3 Hz beat at 220 Hz ... use bigger below
        rig.Set(ParamId.Detune, 63.5f + 38f);                  // (38/63.5)^2*50 = 17.9 cents -> ~2.3 Hz beating at 220 Hz
        rig.Start(57);
        var (l, _) = rig.Run(Fs * 2);
        double lo = double.MaxValue, hi = 0;
        for (int o = 4800; o + 2400 <= l.Length; o += 2400)
        {
            double r = Rms(l, o, 2400);
            lo = Math.Min(lo, r); hi = Math.Max(hi, r);
        }
        Assert.True(hi > 2.5 * lo, $"beating depth {lo}..{hi}");
    }

    [Fact]
    public void Voice_Mod_OnHarmonic_PushesYangAndYinApart()
    {
        // DRONE2 with MOD depth: both oscillators move in opposite harmonic directions -> the sound changes versus depth 0
        double Energy(float depth)
        {
            var rig = new VoiceRig();
            rig.Set(ParamId.Structure, (float)Structure.Drone2);
            rig.Set(ParamId.ModDepth, depth);
            rig.Set(ParamId.ModRate, 60);
            rig.Wave = 25;
            rig.Harmonic = 64;
            rig.Start(57);
            var (l, _) = rig.Run(Fs);
            double d = 0;
            for (int i = 4800; i < l.Length; i++) d += Math.Abs(l[i] - l[i - 1]);
            return d;
        }
        Assert.True(Math.Abs(Energy(127) - Energy(0)) > 0.02 * Energy(0));
    }

    [Fact]
    public void Voice_LfoDestinations_AllChangeTheOutput()
    {
        // base: PAD1 with a rich wave so that every destination is audible
        float[] Render(LfoAssign a, bool lfo2, float depth = 127, bool lfo1Tune = false)
        {
            var rig = new VoiceRig();
            rig.Set(ParamId.Structure, (float)Structure.Pad1);
            rig.Set(ParamId.Pitch, 100); rig.Set(ParamId.Detune, 90); rig.Set(ParamId.Balance, 40);
            rig.Set(ParamId.ModDepth, 70); rig.Set(ParamId.ModRate, 60);
            rig.Filter = FilterMode.LowPass;
            rig.Cutoff = 70; rig.Reso = 60; rig.Harmonic = 50;
            if (lfo1Tune)
            {
                rig.Set(ParamId.Lfo1Assign, (float)LfoAssign.Tune); rig.Set(ParamId.Lfo1Depth, 60); rig.Set(ParamId.Lfo1Rate, 70);
            }
            if (a != LfoAssign.Off)
            {
                rig.Set(lfo2 ? ParamId.Lfo2Assign : ParamId.Lfo1Assign, (float)a);
                rig.Set(lfo2 ? ParamId.Lfo2Depth : ParamId.Lfo1Depth, depth);
                rig.Set(lfo2 ? ParamId.Lfo2Rate : ParamId.Lfo1Rate, 85);
            }
            rig.Start(57);
            var (l, r) = rig.Run(Fs);
            var o = new float[l.Length * 2];
            for (int i = 0; i < l.Length; i++) { o[2 * i] = l[i]; o[2 * i + 1] = r[i]; }
            return o;
        }
        double Diff(float[] a, float[] b)
        {
            double d = 0, s = 0;
            for (int i = 4800; i < a.Length; i++) { d += Math.Abs(a[i] - b[i]); s += Math.Abs(a[i]); }
            return d / s;
        }

        var baseline = Render(LfoAssign.Off, false);
        var baselineTune = Render(LfoAssign.Off, false, lfo1Tune: true);
        foreach (var a in Enum.GetValues<LfoAssign>())
        {
            if (a == LfoAssign.Off) continue;
            if (a is LfoAssign.ShimmerSend or LfoAssign.ReverbSend) continue;          // layer-level, tested separately
            bool fromLfo2 = a is LfoAssign.Lfo1Rate or LfoAssign.Lfo1Depth;
            var withLfo = Render(a, fromLfo2, lfo1Tune: fromLfo2);
            var reference = fromLfo2 ? baselineTune : baseline;
            Assert.True(Diff(withLfo, reference) > 0.01, $"{a} (LFO{(fromLfo2 ? 2 : 1)}) does not change the output: {Diff(withLfo, reference)}");
        }
        // OSC LEVEL is LFO1 only, L1.RT / L1.DP are LFO2 only: ignored from the other LFO
        Assert.True(Diff(Render(LfoAssign.OscLevel, true), baseline) < 1e-6);
        Assert.True(Diff(Render(LfoAssign.Lfo1Rate, false), baseline) < 1e-6);
        Assert.True(Diff(Render(LfoAssign.Lfo1Depth, false), baseline) < 1e-6);
        // LFO2 works on the ordinary destinations too
        Assert.True(Diff(Render(LfoAssign.Level, true), baseline) > 0.01);
        // depth 0 = no effect
        Assert.True(Diff(Render(LfoAssign.Tune, false, depth: 0), baseline) < 1e-6);
    }

    [Fact]
    public void Voice_HarmonicLfo_IsEvaluatedPerSample()
    {
        // the audio-rate HARMONIC modulation of the manual (WH.NS): the LFO value moves inside the 32-sample chunk
        var rig = new VoiceRig();
        rig.Set(ParamId.Structure, (float)Structure.Atmos1);
        rig.Set(ParamId.Lfo1Assign, (float)LfoAssign.Harmonic);
        rig.Set(ParamId.Lfo1Depth, 127);
        rig.Set(ParamId.Lfo1Rate, 127);                         // 30 Hz
        rig.Harmonic = 63;
        rig.Start(57);
        var (a, _) = rig.Run(Fs);
        rig.Set(ParamId.Lfo1Depth, 0);
        var rig2 = new VoiceRig();
        rig2.Set(ParamId.Structure, (float)Structure.Atmos1);
        rig2.Harmonic = 63;
        rig2.Start(57);
        var (b, _) = rig2.Run(Fs);
        double d = 0;
        for (int i = 4800; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
        Assert.True(d > 100);
    }

    [Fact]
    public void Voice_Run_DoesNotAllocate_WithEverythingActive()
    {
        var rig = new VoiceRig();
        rig.Set(ParamId.Structure, (float)Structure.Pad1);
        rig.Set(ParamId.ModDepth, 100);
        rig.Set(ParamId.Lfo1Assign, (float)LfoAssign.Harmonic); rig.Set(ParamId.Lfo1Depth, 100); rig.Set(ParamId.Lfo1Trig, 9);
        rig.Set(ParamId.Lfo2Assign, (float)LfoAssign.Level); rig.Set(ParamId.Lfo2Depth, 100);
        rig.Start(57);
        rig.Run(4096);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) rig.Run(4096);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(after - before < 20 * 2 * 4096 * 4 + 4096, $"allocated {after - before} (only the rig's two result arrays are allowed)");
    }

    // =========================================================================== engine-level (needs the layers to run LayerModulation)

    // The layers are wired to LayerModulation now: fail loudly instead of passing vacuously if that ever regresses.
    private static bool Integrated(AudioEngine e)
    {
        for (int l = 0; l < 4; l++) Assert.IsAssignableFrom<IModulatedLayer>(e.GetAnyLayer(l));
        return true;
    }

    private static LayerModulation ModOf(AudioEngine e, int layer) => ((IModulatedLayer)e.GetAnyLayer(layer)).Mod;

    private static AudioEngine MakeEngine(Action<ParameterStore>? setup = null)
    {
        var p = new ParameterStore();
        for (int l = 0; l < AudioEngine.SynthLayerCount; l++)
        {
            p.Set(l, ParamId.Wave, 3);
            p.Set(l, ParamId.Harmonic, 0);
            p.Set(l, ParamId.Attack, 0);
            p.Set(l, ParamId.Release, 20);
            p.Set(l, ParamId.FilterType, 0);
            if (l > 0) p.Set(l, ParamId.Mute, 1);
        }
        p.Set(3, ParamId.Mute, 1);
        p.Set(GlobalParamId.ReverbType, (float)Sambient.Dsp.Effects.ReverbType.Off);
        p.Set(GlobalParamId.FxType, (float)Sambient.Dsp.Effects.MasterFxType.Off);
        setup?.Invoke(p);
        return new AudioEngine(Fs, p, SharedBank);
    }

    private static float[] Run(AudioEngine e, int frames, int block = 256)
    {
        var all = new float[frames * 2];
        var buf = new float[block * 2];
        for (int done = 0; done < frames; done += block)
        {
            int n = Math.Min(block, frames - done);
            e.Render(buf, n);
            Array.Copy(buf, 0, all, done * 2, n * 2);
        }
        return all;
    }

    private static float[] Left(float[] st) { var l = new float[st.Length / 2]; for (int i = 0; i < l.Length; i++) l[i] = st[2 * i]; return l; }

    [Fact]
    public void Engine_DroneStructure_SetsOctaveMinus2_AndOtherStructuresDoNot()
    {
        var e = MakeEngine();
        e.SetParam(0, ParamId.Octave, 1);
        e.SetParam(0, ParamId.Structure, (float)Structure.Pad1);
        Assert.Equal(1, e.Parameters.Get(0, ParamId.Octave));
        e.SetParam(0, ParamId.Structure, (float)Structure.Drone1);
        Assert.Equal(-2, e.Parameters.Get(0, ParamId.Octave));
        e.SetParam(0, ParamId.Octave, 0);
        e.SetParam(0, ParamId.Structure, (float)Structure.Drone2);
        Assert.Equal(-2, e.Parameters.Get(0, ParamId.Octave));
        e.SetParam(1, ParamId.Octave, 1);
        e.SetParam(1, ParamId.Structure, (float)Structure.Atmos1);
        Assert.Equal(1, e.Parameters.Get(1, ParamId.Octave));
        e.SetParam(3, ParamId.Structure, (float)Structure.Drone1);            // the NOISE layer has no structure
        Assert.Equal(0, e.Parameters.Get(3, ParamId.Octave));
    }

    [Fact]
    public void Engine_EveryStructure_Sounds_WithExpectedOscillators_AndNoNaN()
    {
        foreach (var st in Enum.GetValues<Structure>())
        {
            var e = MakeEngine(p => p.Set(0, ParamId.Structure, (float)st));
            if (!Integrated(e)) return;
            e.NoteOn(0, 60, 100);
            var o = Run(e, 4096);
            Assert.Equal(StructureInfo.OscillatorCount(st), ModOf(e, 0).OscillatorsRendered);
            Assert.True(Rms(Left(o), 2048, 2048) > 0.01, st.ToString());
            foreach (float v in o) Assert.True(float.IsFinite(v));
        }
    }

    [Fact]
    public void Engine_DefaultStructure_IsNotLouderThanTheOldSingleOscillator()
    {
        var e = MakeEngine();
        if (!Integrated(e)) return;
        e.NoteOn(0, 57, 100);
        var o = Left(Run(e, 8192));
        Assert.InRange(Rms(o, 4096, 4096), 0.02, 0.5);
    }

    [Fact]
    public void Engine_LongReleaseForDroneStructures()
    {
        double TailAfter(Structure st)
        {
            var e = MakeEngine(p =>
            {
                p.Set(0, ParamId.Structure, (float)st);
                p.Set(0, ParamId.Octave, 0);
                p.Set(0, ParamId.Release, 90);
            });
            if (!Integrated(e)) return -1;
            e.SetParam(0, ParamId.Octave, 0);
            e.NoteOn(0, 57, 100);
            Run(e, 4800);
            e.NoteOff(0, 57);
            var o = Left(Run(e, Fs * 2));
            return Rms(o, o.Length - 4800, 4800);
        }
        double pad = TailAfter(Structure.Pad1), drone = TailAfter(Structure.Drone2);
        if (pad < 0) return;
        Assert.True(drone > 3 * pad, $"drone tail {drone} vs pad tail {pad}");     // 20 s vs 10 s range at the same knob
    }

    [Fact]
    public void Engine_Lfo_FreeRunning_ContinuesAcrossNotes_InfResetsOnNoteOn()
    {
        var e = MakeEngine(p =>
        {
            p.Set(0, ParamId.Lfo1Assign, (float)LfoAssign.Cutoff);
            p.Set(0, ParamId.Lfo1Depth, 100);
            p.Set(0, ParamId.Lfo1Rate, 64);
            p.Set(0, ParamId.Lfo1Shape, (float)LfoShape.Sine);
            p.Set(0, ParamId.Lfo1Trig, 0);
        });
        if (!Integrated(e)) return;
        var mod = ModOf(e, 0);
        float hz = ParamCurves.LfoRateHz(64);
        Run(e, 4800);                                              // no note at all: the clock still runs
        double expected = Lfo.FastSin((float)((4800.0 * hz / Fs) % 1.0));
        Assert.Equal(expected, mod.Clock1[31], 2);
        e.NoteOn(0, 60, 100);
        Run(e, 9600);
        e.NoteOff(0, 60);
        Run(e, 4800);
        e.NoteOn(0, 64, 100);
        Run(e, 4800);
        expected = Lfo.FastSin((float)((4800.0 * 5 * hz / Fs) % 1.0));
        Assert.Equal(expected, mod.Clock1[31], 2);                 // phase unaffected by two note-ons

        // INF: restarts at note-on
        e.SetParam(0, ParamId.Lfo1Trig, 9);
        Run(e, 256);
        e.NoteOn(0, 67, 100);
        Run(e, 64);
        Assert.True(MathF.Abs(mod.Clock1[31]) < 0.02f, $"{mod.Clock1[31]}");
    }

    [Fact]
    public void Engine_AllLfoDestinations_ChangeTheSynthLayerOutput()
    {
        double Diff(float[] a, float[] b)
        {
            double d = 0, s = 0;
            for (int i = 9600; i < a.Length; i++) { d += Math.Abs(a[i] - b[i]); s += Math.Abs(a[i]); }
            return d / s;
        }
        float[] Render(LfoAssign a, bool lfo2 = false, bool lfo1Tune = false)
        {
            var e = MakeEngine(p =>
            {
                p.Set(0, ParamId.Wave, 8);
                p.Set(0, ParamId.Harmonic, 50);
                p.Set(0, ParamId.Structure, (float)Structure.Pad1);
                p.Set(0, ParamId.Pitch, 100); p.Set(0, ParamId.Detune, 90); p.Set(0, ParamId.Balance, 40);
                p.Set(0, ParamId.ModDepth, 70); p.Set(0, ParamId.ModRate, 60);
                p.Set(0, ParamId.FilterType, (float)FilterType.LowPass);
                p.Set(0, ParamId.Cutoff, 70); p.Set(0, ParamId.Resonance, 60);
                p.Set(0, ParamId.Pan, 10);
                if (lfo1Tune)
                {
                    p.Set(0, ParamId.Lfo1Assign, (float)LfoAssign.Tune); p.Set(0, ParamId.Lfo1Depth, 60); p.Set(0, ParamId.Lfo1Rate, 70);
                }
                if (a != LfoAssign.Off)
                {
                    p.Set(0, lfo2 ? ParamId.Lfo2Assign : ParamId.Lfo1Assign, (float)a);
                    p.Set(0, lfo2 ? ParamId.Lfo2Depth : ParamId.Lfo1Depth, 127);
                    p.Set(0, lfo2 ? ParamId.Lfo2Rate : ParamId.Lfo1Rate, 85);
                }
            });
            if (!Integrated(e)) return Array.Empty<float>();
            e.NoteOn(0, 57, 100);
            return Run(e, Fs);
        }
        var baseline = Render(LfoAssign.Off);
        if (baseline.Length == 0) return;
        var baselineTune = Render(LfoAssign.Off, lfo1Tune: true);
        foreach (var a in Enum.GetValues<LfoAssign>())
        {
            if (a == LfoAssign.Off || a is LfoAssign.ShimmerSend or LfoAssign.ReverbSend) continue;
            bool fromLfo2 = a is LfoAssign.Lfo1Rate or LfoAssign.Lfo1Depth;
            double d = Diff(Render(a, fromLfo2, lfo1Tune: fromLfo2), fromLfo2 ? baselineTune : baseline);
            Assert.True(d > 0.01, $"{a}: {d}");
        }
        // LFO2 on an ordinary destination, and the LFO1-only / LFO2-only restrictions
        Assert.True(Diff(Render(LfoAssign.Level, lfo2: true), baseline) > 0.01);
        Assert.True(Diff(Render(LfoAssign.OscLevel, lfo2: true), baseline) < 1e-6);
        Assert.True(Diff(Render(LfoAssign.Lfo1Rate), baseline) < 1e-6);
    }

    [Fact]
    public void Engine_SendLfos_ModulateTheSendOffsets_AndTheBus()
    {
        foreach (var (assign, shimmer) in new[] { (LfoAssign.ReverbSend, false), (LfoAssign.ShimmerSend, true) })
        {
            var e = MakeEngine(p =>
            {
                p.Set(0, ParamId.Mute, 0);
                p.Set(0, ParamId.Lfo1Assign, (float)assign);
                p.Set(0, ParamId.Lfo1Depth, 127);
                p.Set(0, ParamId.Lfo1Rate, 90);
                p.Set(0, ParamId.ReverbSend, 0); p.Set(0, ParamId.ShimmerSend, 0);
            });
            if (!Integrated(e)) return;
            var mod = ModOf(e, 0);
            e.NoteOn(0, 57, 100);
            float lo = float.MaxValue, hi = float.MinValue;
            double bus = 0;
            var buf = new float[256 * 2];
            for (int i = 0; i < 400; i++)
            {
                e.Render(buf, 256);
                float v = shimmer ? mod.ShimmerSendOffset : mod.ReverbSendOffset;
                lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                if (i > 100) for (int k = 0; k < 256; k++) bus += Math.Abs(shimmer ? e.ShimmerSendBusL[k] : e.ReverbSendBusL[k]);
            }
            Assert.True(hi - lo > 100f, $"{assign}: offset range {lo}..{hi}");
            Assert.True(bus > 1.0, $"{assign}: the modulated send never fed its bus");
            Assert.Equal(0f, shimmer ? mod.ReverbSendOffset : mod.ShimmerSendOffset);   // only the assigned send moves
        }
    }

    [Fact]
    public void Engine_NoiseLayer_PitchDetuneMod_AndLfoDestinations_Work()
    {
        AudioEngine Make(Action<ParameterStore> setup)
        {
            var e = MakeEngine(p =>
            {
                p.Set(3, ParamId.Mute, 0);
                p.Set(3, ParamId.Wave, 1);
                p.Set(3, ParamId.Harmonic, 127);                 // sample only
                p.Set(3, ParamId.Attack, 0); p.Set(3, ParamId.Release, 20);
                p.Set(3, ParamId.FilterType, (float)FilterType.LowPass); p.Set(3, ParamId.Cutoff, 90); p.Set(3, ParamId.Resonance, 60);
                setup(p);
            });
            e.Noise.EnsureBuiltAsync(1).GetAwaiter().GetResult();
            return e;
        }
        float[] Render(Action<ParameterStore> setup)
        {
            var e = Make(setup);
            e.NoteOn(3, 60, 100);
            return Run(e, Fs);
        }
        double Diff(float[] a, float[] b)
        {
            double d = 0, s = 0;
            for (int i = 9600; i < a.Length; i++) { d += Math.Abs(a[i] - b[i]); s += Math.Abs(a[i]); }
            return d / Math.Max(s, 1e-9);
        }
        var e0 = Make(_ => { });
        if (!Integrated(e0)) return;
        var baseline = Render(_ => { });
        Assert.True(Rms(Left(baseline), 9600, 9600) > 0.001, "noise layer silent (sample not built?)");

        Assert.True(Diff(Render(p => p.Set(3, ParamId.Pitch, 120)), baseline) > 0.1);          // PITCH
        Assert.True(Diff(Render(p => p.Set(3, ParamId.Detune, 110)), baseline) > 0.01);        // detune
        Assert.True(Diff(Render(p => { p.Set(3, ParamId.ModDepth, 127); p.Set(3, ParamId.ModRate, 80); }), baseline) > 0.05);   // MOD -> pitch
        Assert.True(Diff(Render(p => p.Set(3, ParamId.Balance, 0)), baseline) < 1e-6);         // BALANCE has no effect here
        Assert.True(Diff(Render(p => p.Set(3, ParamId.Structure, (float)Structure.Drone1)), baseline) < 1e-6);

        foreach (var a in new[]
        {
            LfoAssign.Tune, LfoAssign.Harmonic, LfoAssign.Pitch, LfoAssign.DetuneFeedback, LfoAssign.Cutoff, LfoAssign.Resonance,
            LfoAssign.Pan, LfoAssign.Level, LfoAssign.OscLevel,
        })
        {
            var r = Render(p => { p.Set(3, ParamId.Lfo1Assign, (float)a); p.Set(3, ParamId.Lfo1Depth, 127); p.Set(3, ParamId.Lfo1Rate, 85); p.Set(3, ParamId.Harmonic, 80); });
            var r0 = Render(p => p.Set(3, ParamId.Harmonic, 80));
            Assert.True(Diff(r, r0) > 0.01, $"noise layer {a}: {Diff(r, r0)}");
        }
        // BALANCE LFO: no effect on the noise layer
        var rb = Render(p => { p.Set(3, ParamId.Lfo1Assign, (float)LfoAssign.Balance); p.Set(3, ParamId.Lfo1Depth, 127); p.Set(3, ParamId.Lfo1Rate, 85); });
        Assert.True(Diff(rb, baseline) < 1e-6);
    }

    [Fact]
    public void Engine_Render_DoesNotAllocate_WithAllLfosAndStructuresActive()
    {
        var e = MakeEngine(p =>
        {
            for (int l = 0; l < 4; l++)
            {
                p.Set(l, ParamId.Mute, 0);
                p.Set(l, ParamId.Structure, (float)(l == 0 ? Structure.Atmos2 : l == 1 ? Structure.Pad1 : Structure.Atmos1));
                p.Set(l, ParamId.ModDepth, 100);
                p.Set(l, ParamId.FilterType, (float)FilterType.LowPass);
                p.Set(l, ParamId.Lfo1Assign, (float)LfoAssign.Harmonic); p.Set(l, ParamId.Lfo1Depth, 100); p.Set(l, ParamId.Lfo1Trig, 9);
                p.Set(l, ParamId.Lfo2Assign, (float)LfoAssign.ReverbSend); p.Set(l, ParamId.Lfo2Depth, 100);
            }
        });
        if (!Integrated(e)) return;
        for (int l = 0; l < 4; l++) for (int n = 0; n < 4; n++) e.NoteOn(l, 48 + 4 * n, 100);
        var buf = new float[256 * 2];
        for (int i = 0; i < 40; i++) e.Render(buf, 256);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) e.Render(buf, 256);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task DefaultPatch_ShowcasesTheStructures_AndRendersAudibleFiniteAudio()
    {
        var p = new ParameterStore();
        DefaultPatch.Apply(p);
        Assert.Equal((float)Structure.Drone1, p.Get(0, ParamId.Structure));
        Assert.Equal((float)Structure.Pad1, p.Get(1, ParamId.Structure));
        Assert.Equal((float)Structure.Atmos2, p.Get(2, ParamId.Structure));
        var e = new AudioEngine(Fs, p, SharedBank);
        await Task.WhenAll(SharedBank.EnsureBuiltAsync(3), SharedBank.EnsureBuiltAsync(8));
        foreach (int l in new[] { 0, 1, 2 }) { e.NoteOn(l, 48, 100); e.NoteOn(l, 55, 100); e.NoteOn(l, 64, 100); }
        var o = Run(e, Fs * 3);
        float peak = 0;
        foreach (float v in o) { Assert.True(float.IsFinite(v)); peak = Math.Max(peak, MathF.Abs(v)); }
        Assert.InRange(peak, 0.02f, 1f);
        _out.WriteLine($"default patch peak {peak:F3}, rms {Rms(Left(o), Fs, Fs):F3}");
    }

    [Fact]
    public void Engine_HeavyCase_CpuPerBlock_IsReported()
    {
        // all synth layers PAD1, 16 voices each, every LFO active (the worst case); only asserted loosely in Release
        var e = MakeEngine(p =>
        {
            for (int l = 0; l < 3; l++)
            {
                p.Set(l, ParamId.Mute, 0);
                p.Set(l, ParamId.Wave, 8);
                p.Set(l, ParamId.Structure, (float)Structure.Pad1);
                p.Set(l, ParamId.Release, 60);
                p.Set(l, ParamId.FilterType, (float)FilterType.LowPass);
                p.Set(l, ParamId.Detune, 80); p.Set(l, ParamId.Pitch, 60);
                p.Set(l, ParamId.ModDepth, 100); p.Set(l, ParamId.ModRate, 70); p.Set(l, ParamId.ModShape, (float)LfoShape.SmoothRandom);
                p.Set(l, ParamId.Lfo1Assign, (float)LfoAssign.Harmonic); p.Set(l, ParamId.Lfo1Depth, 90); p.Set(l, ParamId.Lfo1Trig, 9);
                p.Set(l, ParamId.Lfo2Assign, (float)LfoAssign.Cutoff); p.Set(l, ParamId.Lfo2Depth, 90); p.Set(l, ParamId.Lfo2Trig, 9);
            }
        });
        for (int l = 0; l < 3; l++) for (int n = 0; n < 16; n++) e.NoteOn(l, 36 + 3 * n, 100);
        var buf = new float[256 * 2];
        for (int i = 0; i < 100; i++) e.Render(buf, 256);          // warm up (JIT)
        const int blocks = 2000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < blocks; i++) e.Render(buf, 256);
        sw.Stop();
        double msPerBlock = sw.Elapsed.TotalMilliseconds / blocks;
        double budget = 256.0 / Fs * 1000.0;
        _out.WriteLine($"3 x PAD1 x 16 voices, all LFOs: {msPerBlock:F3} ms per 256-frame block (budget {budget:F2} ms, {100 * msPerBlock / budget:F0} % of real time), active voices {e.ActiveVoices}");
#if !DEBUG
        if (Integrated(e)) Assert.True(msPerBlock < budget * 0.6, $"{msPerBlock} ms per block");
#endif
    }
}
