using Sambient.Dsp.Effects;
using Xunit.Abstractions;

namespace Sambient.Tests;

public class EffectsTests
{
    private const int Fs = 48000;
    private readonly ITestOutputHelper _out;
    public EffectsTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> ReverbTypes() =>
        Enum.GetValues<ReverbType>().Where(t => t != ReverbType.Off).Select(t => new object[] { t });

    public static IEnumerable<object[]> FxTypes() =>
        Enum.GetValues<MasterFxType>().Where(t => t != MasterFxType.Off).Select(t => new object[] { t });

    // ---------------------------------------------------------------- helpers

    private static double Goertzel(float[] x, int start, int count, double freq)
    {
        double w = 2 * Math.PI * freq / Fs, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = 0; i < count; i++)
        {
            // Hann window to keep leakage low
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (count - 1));
            double s0 = x[start + i] * win + c * s1 - s2;
            s2 = s1; s1 = s0;
        }
        double re = s1 - s2 * Math.Cos(w), im = s2 * Math.Sin(w);
        return 2 * Math.Sqrt(re * re + im * im) / count * 2; // window-gain compensated amplitude
    }

    private static float[] Noise(int n, float amp, int seed)
    {
        var rng = new Random(seed);
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 2 - 1) * amp;
        return x;
    }

    /// <summary>Runs the reverb over the given inputs in blocks and returns the wet L/R.</summary>
    private static (float[] L, float[] R) RunReverb(ReverbType type, float mix, float[] revL, float[] revR, float[] shL, float[] shR, int block = 256)
    {
        var fx = new ShimmerReverb(Fs);
        fx.SetType(type);
        fx.SetMix(mix);
        fx.Reset();
        int n = revL.Length;
        var wl = new float[n]; var wr = new float[n];
        for (int o = 0; o < n; o += block)
        {
            int m = Math.Min(block, n - o);
            fx.Process(revL.AsSpan(o, m), revR.AsSpan(o, m), shL.AsSpan(o, m), shR.AsSpan(o, m), wl.AsSpan(o, m), wr.AsSpan(o, m));
        }
        return (wl, wr);
    }

    /// <summary>T10 decay slope (-5 dB to -15 dB of a 50 ms energy envelope) extrapolated to 60 dB.</summary>
    private static double MeasureRt60(float[] l, float[] r)
    {
        int bin = Fs / 20;
        int nb = l.Length / bin;
        var db = new double[nb];
        for (int b = 0; b < nb; b++)
        {
            double e = 0;
            for (int i = b * bin; i < (b + 1) * bin; i++) e += l[i] * (double)l[i] + r[i] * (double)r[i];
            db[b] = 10 * Math.Log10(e / bin + 1e-30);
        }
        int pk = 0;
        for (int b = 1; b < nb; b++) if (db[b] > db[pk]) pk = b;
        int i5 = -1, i15 = -1;
        for (int b = pk; b < nb; b++)
        {
            if (i5 < 0 && db[b] <= db[pk] - 5) i5 = b;
            if (db[b] <= db[pk] - 15) { i15 = b; break; }
        }
        if (i5 < 0 || i15 < 0) return double.PositiveInfinity;
        // linear regression over [i5, i15]
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int cnt = i15 - i5 + 1;
        for (int b = i5; b <= i15; b++) { double t = b * 0.05; sx += t; sy += db[b]; sxx += t * t; sxy += t * db[b]; }
        double slope = (cnt * sxy - sx * sy) / (cnt * sxx - sx * sx); // dB per second (negative)
        return -60.0 / slope;
    }

    private static float[] Impulse(int n) { var x = new float[n]; x[0] = 1f; return x; }

    // ---------------------------------------------------------------- reverb

    [Theory, MemberData(nameof(ReverbTypes))]
    public void Reverb_Stable_Bounded_30s_ContinuousNoise(ReverbType type)
    {
        int n = 30 * Fs;
        var a = Noise(n, 1.0f, 1); var b = Noise(n, 1.0f, 2);
        var c = Noise(n, 1.0f, 3); var d = Noise(n, 1.0f, 4);
        var (wl, wr) = RunReverb(type, 127f, a, b, c, d);
        float peak = 0;
        for (int i = 0; i < n; i++)
        {
            Assert.True(float.IsFinite(wl[i]) && float.IsFinite(wr[i]), $"non-finite at {i}");
            peak = Math.Max(peak, Math.Max(Math.Abs(wl[i]), Math.Abs(wr[i])));
        }
        _out.WriteLine($"{type}: peak {peak:F2} with full-scale noise on all four sends");
        Assert.True(peak < 20f, $"peak {peak}");
        Assert.True(peak > 0.05f);
    }

    [Theory, MemberData(nameof(ReverbTypes))]
    public void Reverb_Impulse_Bounded_And_Finite(ReverbType type)
    {
        int n = 30 * Fs;
        var imp = Impulse(n);
        var z = new float[n];
        var (wl, wr) = RunReverb(type, 127f, imp, imp, imp, z, 4096);
        for (int i = 0; i < n; i++)
        {
            Assert.True(float.IsFinite(wl[i]) && float.IsFinite(wr[i]));
            Assert.True(Math.Abs(wl[i]) < 5f && Math.Abs(wr[i]) < 5f);
        }
    }

    [Theory]
    [InlineData(ReverbType.SmallL, 0.9, 2.2)]
    [InlineData(ReverbType.SmallM, 1.0, 2.4)]
    [InlineData(ReverbType.SmallH, 1.2, 2.6)]
    [InlineData(ReverbType.LargeL, 3.8, 7.5)]
    [InlineData(ReverbType.LargeM, 4.0, 8.0)]
    [InlineData(ReverbType.LargeH, 4.5, 8.5)]
    [InlineData(ReverbType.InfinityL, 30.0, 1e9)]
    [InlineData(ReverbType.InfinityM, 30.0, 1e9)]
    [InlineData(ReverbType.InfinityH, 30.0, 1e9)]
    public void Reverb_Rt60_InExpectedRange(ReverbType type, double lo, double hi)
    {
        int secs = type >= ReverbType.InfinityL ? 40 : 20;
        int n = secs * Fs;
        var z = new float[n];
        var (wl, wr) = RunReverb(type, 127f, Impulse(n), Impulse(n), z, z);
        double rt = MeasureRt60(wl, wr);
        _out.WriteLine($"{type}: RT60 (T10-extrapolated) = {rt:F2} s");
        Assert.InRange(rt, lo, hi);
    }

    [Theory]
    [InlineData(ReverbType.SmallL, ReverbType.SmallH)]
    [InlineData(ReverbType.LargeL, ReverbType.LargeH)]
    [InlineData(ReverbType.InfinityL, ReverbType.InfinityH)]
    public void Reverb_L_IsDarkerThan_H(ReverbType dark, ReverbType bright)
    {
        int n = 6 * Fs;
        var nz = Noise(Fs / 4, 0.8f, 7);
        var inp = new float[n]; Array.Copy(nz, inp, nz.Length);
        var z = new float[n];
        double Bright(ReverbType t)
        {
            var (wl, _) = RunReverb(t, 127f, inp, inp, z, z);
            double e = 0, de = 0;
            for (int i = Fs; i < n; i++) { e += wl[i] * (double)wl[i]; double d = wl[i] - wl[i - 1]; de += d * d; }
            return de / e; // ~ spectral centroid proxy (first-difference energy ratio)
        }
        double bd = Bright(dark), bb = Bright(bright);
        _out.WriteLine($"{dark}: {bd:F3}  {bright}: {bb:F3}");
        Assert.True(bd < bb * 0.8);
    }

    [Fact]
    public void Reverb_Shimmer_ProducesOctaveUp()
    {
        int n = 4 * Fs;
        var sine = new float[n];
        for (int i = 0; i < n; i++) sine[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / Fs);
        var z = new float[n];
        var (wl, wr) = RunReverb(ReverbType.LargeM, 127f, z, z, sine, sine);
        // The two-grain shifter spreads a pure tone into a narrow cluster around 880 Hz, so measure band power.
        double Band(float[] x, double c) { double p = 0; for (double f = c - 60; f <= c + 60; f += 3) { double a = Goertzel(x, 2 * Fs, Fs, f); p += a * a / 2; } return p; }
        double p880 = Band(wl, 880), p440 = Band(wl, 440);
        double rms = 0; for (int i = 2 * Fs; i < 3 * Fs; i++) rms += wl[i] * (double)wl[i];
        _out.WriteLine($"shimmer: power around 880 Hz {p880:E3}, around 440 Hz {p440:E3}, total wet power {rms / Fs:E3}");
        Assert.True(p880 > 1e-4, $"880 band power {p880}");
        Assert.True(p880 > 5 * p440);

        // Control: the same sine on the plain reverb send has no 880 Hz content.
        var (cl, _) = RunReverb(ReverbType.LargeM, 127f, sine, sine, z, z);
        Assert.True(Band(cl, 440) > 10 * Band(cl, 880));
    }

    [Fact]
    public void Reverb_MixZero_And_Off_AreSilent()
    {
        int n = 2 * Fs;
        var x = Noise(n, 0.5f, 5);
        var (wl, wr) = RunReverb(ReverbType.LargeM, 0f, x, x, x, x);
        Assert.True(wl.All(v => v == 0f) && wr.All(v => v == 0f));
        var (ol, orr) = RunReverb(ReverbType.Off, 127f, x, x, x, x);
        Assert.True(ol.All(v => v == 0f) && orr.All(v => v == 0f));
    }

    [Fact]
    public void Reverb_TypeSwitching_NoNaN_NoLoudClick()
    {
        var fx = new ShimmerReverb(Fs);
        fx.SetMix(100f);
        var types = Enum.GetValues<ReverbType>();
        var rng = new Random(3);
        var inp = new float[256]; var z = new float[256]; var wl = new float[256]; var wr = new float[256];
        float prev = 0, maxJump = 0;
        for (int blk = 0; blk < 1500; blk++)
        {
            if (blk % 30 == 0) fx.SetType(types[rng.Next(types.Length)]);
            for (int i = 0; i < 256; i++) inp[i] = 0.3f * (float)Math.Sin(2 * Math.PI * 330 * (blk * 256 + i) / Fs);
            fx.Process(inp, inp, inp, z, wl, wr);
            for (int i = 0; i < 256; i++)
            {
                Assert.True(float.IsFinite(wl[i]) && float.IsFinite(wr[i]));
                maxJump = Math.Max(maxJump, Math.Abs(wl[i] - prev)); prev = wl[i];
            }
        }
        _out.WriteLine($"max sample jump during type switching: {maxJump:F3}");
        Assert.True(maxJump < 0.5f);
    }

    // ---------------------------------------------------------------- master FX

    private static float[] SineBuf(int n, double hz, float amp)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = amp * (float)Math.Sin(2 * Math.PI * hz * i / Fs);
        return x;
    }

    private static void RunFx(MasterFx fx, float[] l, float[] r, int block = 256)
    {
        for (int o = 0; o < l.Length; o += block)
        {
            int m = Math.Min(block, l.Length - o);
            fx.Process(l.AsSpan(o, m), r.AsSpan(o, m));
        }
    }

    [Fact]
    public void MasterFx_Off_IsBitExact()
    {
        var fx = new MasterFx(Fs);
        fx.SetSpeed(100); fx.SetAmount(127);
        var l = Noise(Fs, 1f, 1); var r = Noise(Fs, 1f, 2);
        var l0 = (float[])l.Clone(); var r0 = (float[])r.Clone();
        RunFx(fx, l, r);
        Assert.Equal(l0, l); Assert.Equal(r0, r);
        // and again after having used an effect and switched back (after the crossfade has completed)
        fx.SetType(MasterFxType.Chorus);
        RunFx(fx, Noise(Fs, 0.3f, 8), Noise(Fs, 0.3f, 9));
        fx.SetType(MasterFxType.Off);
        RunFx(fx, Noise(Fs, 0.3f, 8), Noise(Fs, 0.3f, 9));
        var l2 = Noise(Fs, 1f, 3); var r2 = Noise(Fs, 1f, 4);
        var l20 = (float[])l2.Clone(); var r20 = (float[])r2.Clone();
        RunFx(fx, l2, r2);
        Assert.Equal(l20, l2); Assert.Equal(r20, r2);
    }

    [Theory, MemberData(nameof(FxTypes))]
    public void MasterFx_Stable_Bounded_30s_ExtremeSettings(MasterFxType type)
    {
        var fx = new MasterFx(Fs);
        fx.SetType(type);
        var rng = new Random(11);
        int n = 30 * Fs;
        var l = new float[256]; var r = new float[256];
        float peak = 0;
        int blocks = n / 256;
        for (int b = 0; b < blocks; b++)
        {
            if (b % 94 == 0) // ~every 0.5 s jump to a random extreme
            {
                fx.SetSpeed(rng.Next(2) == 0 ? 0 : 127);
                fx.SetAmount(rng.Next(3) switch { 0 => 0, 1 => 127, _ => rng.Next(128) });
            }
            for (int i = 0; i < 256; i++)
            {
                float v = (b % 40 == 0 && i == 0) ? 1f : (float)(rng.NextDouble() * 2 - 1) * (b % 7 < 3 ? 1f : 0.1f);
                l[i] = v; r[i] = -v;
            }
            fx.Process(l, r);
            for (int i = 0; i < 256; i++)
            {
                Assert.True(float.IsFinite(l[i]) && float.IsFinite(r[i]), $"{type} non-finite");
                peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            }
        }
        _out.WriteLine($"{type}: peak {peak:F2}");
        Assert.True(peak < 8f);
    }

    [Fact]
    public void MasterFx_BigBlocks_4096_Work()
    {
        foreach (var t in Enum.GetValues<MasterFxType>())
        {
            var fx = new MasterFx(Fs);
            fx.SetType(t); fx.SetSpeed(80); fx.SetAmount(100);
            var l = Noise(4096, 0.5f, 1); var r = Noise(4096, 0.5f, 2);
            for (int k = 0; k < 20; k++) fx.Process(l, r);
            Assert.True(l.All(float.IsFinite) && r.All(float.IsFinite));
        }
    }

    [Fact]
    public void TiltEq_AtCentre_IsFlat_And_Tilts_Sensibly()
    {
        int n = 2 * Fs;
        double[] freqs = { 80, 300, 1000, 3000, 8000, 14000 };
        foreach (float speed in new[] { 10f, 64f, 120f })
        {
            var fx = new MasterFx(Fs);
            fx.SetType(MasterFxType.TiltEq);
            fx.SetSpeed(speed); fx.SetAmount(63.5f);
            foreach (double f in freqs)
            {
                var l = SineBuf(n, f, 0.5f); var r = (float[])l.Clone();
                var fx2 = new MasterFx(Fs); fx2.SetType(MasterFxType.TiltEq); fx2.SetSpeed(speed); fx2.SetAmount(63.5f);
                RunFx(fx2, l, r);
                double gain = Goertzel(l, Fs / 2, Fs, f) / 0.5;
                double db = 20 * Math.Log10(gain);
                Assert.InRange(db, -0.5, 0.5);
            }
        }
        // Brighter: high frequencies louder than lows; darker: the reverse.
        double Tilt(float amount)
        {
            var fx = new MasterFx(Fs);
            fx.SetType(MasterFxType.TiltEq); fx.SetSpeed(64); fx.SetAmount(amount);
            var lo = SineBuf(n, 100, 0.5f); var hi = SineBuf(n, 10000, 0.5f);
            var a = (float[])lo.Clone(); var b = (float[])hi.Clone();
            RunFx(fx, lo, a);
            var fx2 = new MasterFx(Fs);
            fx2.SetType(MasterFxType.TiltEq); fx2.SetSpeed(64); fx2.SetAmount(amount);
            RunFx(fx2, hi, b);
            return 20 * Math.Log10(Goertzel(hi, Fs / 2, Fs, 10000) / Goertzel(lo, Fs / 2, Fs, 100));
        }
        double bright = Tilt(127), dark = Tilt(0);
        _out.WriteLine($"tilt hi-lo difference: bright {bright:F1} dB, dark {dark:F1} dB");
        Assert.True(bright > 6 && dark < -6);
    }

    [Fact]
    public void Overdrive_AddsHarmonics()
    {
        int n = Fs;
        float[] Run(float speed, float amount)
        {
            var fx = new MasterFx(Fs);
            fx.SetType(MasterFxType.Overdrive); fx.SetSpeed(speed); fx.SetAmount(amount);
            var l = SineBuf(n, 220, 0.3f); var r = (float[])l.Clone();
            RunFx(fx, l, r);
            return l;
        }
        var wet = Run(120, 127);
        var dry = Run(120, 0);
        double h3Wet = Goertzel(wet, Fs / 2, Fs / 2, 660), h3Dry = Goertzel(dry, Fs / 2, Fs / 2, 660);
        double h5Wet = Goertzel(wet, Fs / 2, Fs / 2, 1100);
        _out.WriteLine($"3rd harmonic dry {h3Dry:E2} wet {h3Wet:E2}, 5th wet {h5Wet:E2}");
        Assert.True(h3Wet > 0.05);
        Assert.True(h3Wet > 100 * h3Dry);
        Assert.True(wet.Max() < 1.5f);
    }

    [Fact]
    public void TapeDelay_EchoAtExpectedTime()
    {
        const float speed = 63.5f;
        double expected = 0.03 * Math.Pow(2.0 / 0.03, speed / 127.0);
        var fx = new MasterFx(Fs);
        fx.SetType(MasterFxType.TapeDelay); fx.SetSpeed(speed); fx.SetAmount(64);
        int n = 2 * Fs;
        var l = new float[n]; var r = new float[n];
        // the impulse comes after the ~15 ms fade-in that follows a type switch (click-free delay-line start)
        const int at = 2000;
        l[at] = 1f; r[at] = 1f;
        RunFx(fx, l, r);
        int lo = (int)((expected - 0.05) * Fs), hi = (int)((expected + 0.05) * Fs);
        int pk = lo;
        for (int i = lo; i < hi; i++) if (Math.Abs(l[i]) > Math.Abs(l[pk])) pk = i;
        double t = (pk - at) / (double)Fs;
        _out.WriteLine($"echo at {t * 1000:F1} ms (expected {expected * 1000:F1} ms), amplitude {l[pk]:F3}");
        Assert.InRange(t, expected - 0.004, expected + 0.004);
        Assert.True(Math.Abs(l[pk]) > 0.3f);
        // second (ping-pong) repeat appears on the right at ~2t
        int p2 = (int)((2 * expected) * Fs) + at;
        float m = 0; for (int i = p2 - 600; i < p2 + 600 && i < n; i++) m = Math.Max(m, Math.Abs(r[i]));
        Assert.True(m > 0.05f);
    }

    [Fact]
    public void ReverseDelay_And_Chorus_And_Crush_ProduceWetSignal()
    {
        foreach (var t in new[] { MasterFxType.ReverseDelay, MasterFxType.Chorus, MasterFxType.Crush })
        {
            var fx = new MasterFx(Fs);
            fx.SetType(t); fx.SetSpeed(80); fx.SetAmount(110);
            var l = SineBuf(Fs, 300, 0.4f); var r = (float[])l.Clone();
            var dry = (float[])l.Clone();
            RunFx(fx, l, r);
            double diff = 0;
            for (int i = Fs / 2; i < Fs; i++) diff += Math.Abs(l[i] - dry[i]);
            Assert.True(diff > 1.0, $"{t} did nothing");
        }
    }

    [Fact]
    public void MasterFx_TypeSwitching_NoLoudClicks()
    {
        var fx = new MasterFx(Fs);
        fx.SetSpeed(60); fx.SetAmount(60);
        var types = Enum.GetValues<MasterFxType>();
        var l = new float[256]; var r = new float[256];
        float prev = 0, maxJump = 0;
        int blk = 0;
        for (int round = 0; round < 3; round++)
            foreach (var t in types)
            {
                fx.SetType(t);
                for (int k = 0; k < 60; k++, blk++)
                {
                    for (int i = 0; i < 256; i++) l[i] = r[i] = 0.3f * (float)Math.Sin(2 * Math.PI * 220 * (blk * 256 + i) / Fs);
                    fx.Process(l, r);
                    for (int i = 0; i < 256; i++) { maxJump = Math.Max(maxJump, Math.Abs(l[i] - prev)); prev = l[i]; Assert.True(float.IsFinite(l[i])); }
                }
            }
        _out.WriteLine($"max sample jump over all type switches: {maxJump:F3} (a 220 Hz 0.3 sine has {0.3 * 2 * Math.PI * 220 / Fs:F3})");
        Assert.True(maxJump < 0.35f);
    }

    // ---------------------------------------------------------------- allocation

    [Theory, MemberData(nameof(ReverbTypes))]
    public void Reverb_NoAllocations(ReverbType type)
    {
        var fx = new ShimmerReverb(Fs);
        fx.SetType(type); fx.SetMix(90f);
        var a = Noise(256, 0.3f, 1);
        var wl = new float[256]; var wr = new float[256];
        for (int i = 0; i < 400; i++) fx.Process(a, a, a, a, wl, wr);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 400; i++)
        {
            if (i % 100 == 0) { fx.SetMix(40f + i % 7); fx.SetType(type); }
            fx.Process(a, a, a, a, wl, wr);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory, MemberData(nameof(FxTypes))]
    public void MasterFx_NoAllocations(MasterFxType type)
    {
        var fx = new MasterFx(Fs);
        fx.SetType(type); fx.SetSpeed(70); fx.SetAmount(90);
        var l = Noise(256, 0.3f, 1); var r = Noise(256, 0.3f, 2);
        for (int i = 0; i < 400; i++) fx.Process(l, r);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 400; i++)
        {
            if (i % 50 == 0) { fx.SetSpeed(i % 127); fx.SetAmount(127 - i % 127); fx.SetTempo(100 + i); }
            if (i == 300) { fx.SetType(MasterFxType.Off); fx.SetType(type); } // switching must not allocate either
            fx.Process(l, r);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Cpu_Timing_Report()
    {
        var a = Noise(256, 0.3f, 1);
        var wl = new float[256]; var wr = new float[256];
        foreach (var t in new[] { ReverbType.SmallM, ReverbType.LargeM, ReverbType.InfinityM })
        {
            var fx = new ShimmerReverb(Fs); fx.SetType(t); fx.SetMix(90);
            for (int i = 0; i < 200; i++) fx.Process(a, a, a, a, wl, wr);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++) fx.Process(a, a, a, a, wl, wr);
            _out.WriteLine($"reverb {t}: {sw.Elapsed.TotalMilliseconds / 2000 * 1000:F1} us per 256-frame block");
        }
        foreach (var t in Enum.GetValues<MasterFxType>().Where(t => t != MasterFxType.Off))
        {
            var fx = new MasterFx(Fs); fx.SetType(t); fx.SetSpeed(70); fx.SetAmount(90);
            var l = Noise(256, 0.3f, 1); var r = Noise(256, 0.3f, 2);
            for (int i = 0; i < 200; i++) fx.Process(l, r);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++) fx.Process(l, r);
            _out.WriteLine($"fx {t}: {sw.Elapsed.TotalMilliseconds / 2000 * 1000:F1} us per 256-frame block");
        }
    }
}
