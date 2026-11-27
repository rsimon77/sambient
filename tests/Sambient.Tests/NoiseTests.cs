using System.Numerics;
using Sambient.Dsp.Noise;

namespace Sambient.Tests;

public class NoiseTests
{
    private static readonly Lazy<NoiseSample[]> All = new(() =>
        Enumerable.Range(1, NoiseBank.SlotCount).Select(NoiseTextures.Generate).ToArray());

    private static NoiseSample S(int slot) => All.Value[slot - 1];

    public static IEnumerable<object[]> Slots() => Enumerable.Range(1, 8).Select(i => new object[] { i });

    private static double Rms(NoiseSample s)
    {
        double e = 0;
        for (int i = 0; i < s.Length; i++) e += (double)s.Left[i] * s.Left[i] + (double)s.Right[i] * s.Right[i];
        return Math.Sqrt(e / (2.0 * s.Length));
    }

    [Fact]
    public void Generation_IsDeterministic()
    {
        for (int slot = 1; slot <= 8; slot++)
        {
            var a = NoiseTextures.Generate(slot);
            Assert.True(a.Left.AsSpan().SequenceEqual(S(slot).Left), $"slot {slot} L");
            Assert.True(a.Right.AsSpan().SequenceEqual(S(slot).Right), $"slot {slot} R");
        }
    }

    [Theory, MemberData(nameof(Slots))]
    public void Texture_BasicProperties(int slot)
    {
        var s = S(slot);
        Assert.Equal(NoiseBank.DefaultNames[slot - 1], s.Name);
        Assert.Equal(32000, s.SampleRate);
        Assert.Equal(256000, s.Length);
        Assert.Equal(256000, s.Right.Length);

        double peak = 0, dcL = 0, dcR = 0;
        for (int i = 0; i < s.Length; i++)
        {
            Assert.False(float.IsNaN(s.Left[i]) || float.IsInfinity(s.Left[i]) || float.IsNaN(s.Right[i]) || float.IsInfinity(s.Right[i]));
            peak = Math.Max(peak, Math.Max(Math.Abs(s.Left[i]), Math.Abs(s.Right[i])));
            dcL += s.Left[i]; dcR += s.Right[i];
        }
        double rms = Rms(s);
        double rmsDb = 20 * Math.Log10(rms), peakDb = 20 * Math.Log10(peak);
        Assert.InRange(rmsDb, -26.0, -18.0);
        Assert.True(peakDb <= -0.99, $"peak {peakDb} dB");
        
        Assert.True(Math.Abs(dcL / s.Length) < 1e-4 && Math.Abs(dcR / s.Length) < 1e-4, "DC tight");
    }

    [Theory, MemberData(nameof(Slots))]
    public void Texture_LoopSeamIsContinuous(int slot)
    {
        var s = S(slot);
        foreach (var ch in new[] { s.Left, s.Right })
        {
            int n = ch.Length;
            double d2 = 0;
            for (int i = 1; i < n; i++) { double d = ch[i] - ch[i - 1]; d2 += d * d; }
            double typical = Math.Sqrt(d2 / (n - 1));
            double seam = Math.Abs(ch[n - 1] - ch[0]);
            Assert.True(seam < 5 * typical + 1e-6, $"{s.Name}: seam {seam} vs typical step {typical}");
            double slopeSeam = Math.Abs((ch[0] - ch[n - 1]) - (ch[n - 1] - ch[n - 2]));
            Assert.True(slopeSeam < 8 * typical + 1e-6, $"{s.Name}: seam slope jump {slopeSeam} vs {typical}");
        }
    }

    [Theory, MemberData(nameof(Slots))]
    public void Texture_HasStereoContent(int slot)
    {
        var s = S(slot);
        double ll = 0, rr = 0, lr = 0;
        for (int i = 0; i < s.Length; i++) { ll += s.Left[i] * s.Left[i]; rr += s.Right[i] * s.Right[i]; lr += s.Left[i] * s.Right[i]; }
        double corr = lr / Math.Sqrt(ll * rr);
        Assert.InRange(corr, -0.3, 0.95);
    }

    // ---- spectral helpers

    private static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            var wl = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len)
            {
                Complex w = 1;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k]; var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v; a[i + k + len / 2] = u - v; w *= wl;
                }
            }
        }
    }

    /// <summary>Welch power spectrum (Hann, 50% overlap) of the left channel; bin spacing = sr / seg.</summary>
    private static double[] Spectrum(NoiseSample s, int seg)
    {
        var p = new double[seg / 2 + 1];
        var buf = new Complex[seg];
        for (int start = 0; start + seg <= s.Length; start += seg / 2)
        {
            for (int i = 0; i < seg; i++) buf[i] = s.Left[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / seg));
            Fft(buf);
            for (int k = 0; k < p.Length; k++) p[k] += buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
        }
        return p;
    }

    [Fact]
    public void Rumble_EnergyMostlyBelow300Hz()
    {
        var p = Spectrum(S(3), 4096);
        double bin = 32000.0 / 4096, low = 0, all = 0;
        for (int k = 0; k < p.Length; k++) { all += p[k]; if (k * bin < 300) low += p[k]; }
        Assert.True(low / all > 0.9, $"low fraction {low / all}");
    }

    [Fact]
    public void Frost_SpectralCentroidIsHigh()
    {
        var p = Spectrum(S(4), 4096);
        double bin = 32000.0 / 4096, num = 0, den = 0;
        for (int k = 0; k < p.Length; k++) { num += k * bin * p[k]; den += p[k]; }
        Assert.True(num / den > 5000, $"centroid {num / den}");
    }

    [Fact]
    public void Flute_HasSpectralPeakAtC4()
    {
        var p = Spectrum(S(8), 32768);
        double bin = 32000.0 / 32768;
        int best = 0;
        for (int k = (int)(100 / bin); k < (int)(3000 / bin); k++) if (p[k] > p[best]) best = k;
        Assert.InRange(best * bin, 261.63 - 5, 261.63 + 5);
    }

    // ---- bank

    [Fact]
    public async Task Bank_LazyBuildAndPublish()
    {
        var bank = new NoiseBank();
        for (int i = 1; i <= 8; i++) { Assert.False(bank.IsReady(i)); Assert.Null(bank.Get(i)); }
        var t1 = bank.EnsureBuiltAsync(2);
        Assert.Same(t1, bank.EnsureBuiltAsync(2));
        await t1;
        Assert.True(bank.IsReady(2));
        Assert.False(bank.IsReady(3));
        var s = bank.Get(2)!;
        Assert.Equal("FIRE", s.Name);
        Assert.Same(s, bank.Get(2));
        Assert.Null(bank.Get(0));
        Assert.Null(bank.Get(9));
    }

    [Fact]
    public async Task Bank_SetOverridesAndSurvivesBuild()
    {
        var bank = new NoiseBank();
        var user = new NoiseSample("USER", 32000, new float[100], new float[100]);
        bank.Set(5, user);
        Assert.Same(user, bank.Get(5));
        await bank.EnsureBuiltAsync(5);
        Assert.Same(user, bank.Get(5));
    }

    // ---- player

    private static NoiseSample Ramp(int n)
    {
        var l = new float[n]; var r = new float[n];
        for (int i = 0; i < n; i++) { l[i] = MathF.Sin(i * 0.05f) * 0.5f; r[i] = MathF.Cos(i * 0.03f) * 0.5f; }
        return new NoiseSample("T", 32000, l, r);
    }

    [Fact]
    public void Player_Rate1_ReproducesSample_Loop()
    {
        var s = Ramp(2000);
        var p = new SamplePlayer();
        p.SetSample(s); p.SetMode(SamplePlayMode.Loop); p.SetRate(1.0); p.Start();
        for (int i = 0; i < 3000; i++)
        {
            p.Next(out var l, out var r);
            if (i < 128) continue; // fade-in
            Assert.Equal(s.Left[i % 2000], l, 1e-5);
            Assert.Equal(s.Right[i % 2000], r, 1e-5);
        }
    }

    [Fact]
    public void Player_Rate2_HalvesDuration_OneShot()
    {
        var s = Ramp(4000);
        int Count(double rate)
        {
            var p = new SamplePlayer();
            p.SetSample(s); p.SetMode(SamplePlayMode.OneShot); p.SetRate(rate); p.Start();
            int n = 0;
            while (p.IsPlaying && n < 100000) { p.Next(out _, out _); n++; }
            return n;
        }
        int a = Count(1.0), b = Count(2.0);
        Assert.InRange(a, 3990, 4001);
        Assert.InRange(b, a / 2 - 2, a / 2 + 2);
    }

    [Fact]
    public void Player_OneShot_EndsSilentWithoutClick()
    {
        var s = NoiseTextures.Generate(4);
        var p = new SamplePlayer();
        p.SetSample(s); p.SetMode(SamplePlayMode.OneShot); p.SetRate(1.0); p.Start();
        float prev = 0; float lastOut = 0; int n = 0;
        while (p.IsPlaying) { p.Next(out var l, out _); prev = lastOut; lastOut = l; n++; }
        Assert.True(Math.Abs(lastOut) < 0.02f, $"last {lastOut}");
        Assert.True(n > s.Length - 5);
        p.Next(out var a, out var b);
        Assert.Equal(0f, a); Assert.Equal(0f, b);
        Assert.False(p.IsPlaying);
    }

    [Fact]
    public void Player_LoopWrap_IsContinuous()
    {
        var s = S(5);
        var p = new SamplePlayer();
        p.SetSample(s); p.SetMode(SamplePlayMode.Loop); p.SetRate(1.37); p.Start();
        double d2 = 0; float prev = 0; int cnt = 0; double maxAtWrap = 0;
        int total = (int)(s.Length / 1.37) + 2000;
        for (int i = 0; i < total; i++)
        {
            p.Next(out var l, out _);
            if (i > 200) { double d = Math.Abs(l - prev); d2 += d * d; cnt++; if (i > (int)(s.Length / 1.37) - 3 && i < (int)(s.Length / 1.37) + 4) maxAtWrap = Math.Max(maxAtWrap, d); }
            prev = l;
        }
        double typical = Math.Sqrt(d2 / cnt);
        Assert.True(maxAtWrap < 6 * typical, $"wrap step {maxAtWrap} vs {typical}");
        Assert.True(p.IsPlaying);
    }

    [Fact]
    public void Player_ExtremeRatesStayFiniteAndPlay()
    {
        var s = S(1);
        var p = new SamplePlayer();
        p.SetSample(s); p.SetMode(SamplePlayMode.Loop); p.Start();
        foreach (var rate in new[] { 1.0 / 16, 16.0, 0.0 })
        {
            p.SetRate(rate);
            for (int i = 0; i < 50000; i++) { p.Next(out var l, out var r); Assert.False(float.IsNaN(l) || float.IsNaN(r)); }
        }
    }

    [Fact]
    public void Player_NullSample_IsSilent()
    {
        var p = new SamplePlayer();
        p.SetSample(null); p.SetRate(1); p.Start();
        Assert.False(p.IsPlaying);
        p.Next(out var l, out var r);
        Assert.Equal(0f, l); Assert.Equal(0f, r);
    }

    [Fact]
    public void Player_DoesNotAllocate()
    {
        var s = S(1);
        var p = new SamplePlayer();
        p.SetSample(s); p.SetMode(SamplePlayMode.Loop); p.SetRate(1.1); p.Start();
        float acc = 0;
        for (int i = 0; i < 1000; i++) { p.Next(out var l, out var r); acc += l + r; }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200000; i++) { p.SetRate(1.0 + 0.001 * (i % 10)); p.Next(out var l, out var r); acc += l + r; }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.False(float.IsNaN(acc));
    }
}
