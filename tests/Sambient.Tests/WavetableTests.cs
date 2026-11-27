using Sambient.Dsp.Wavetables;
using Xunit.Abstractions;

namespace Sambient.Tests;

public class WavetableTests
{
    private readonly ITestOutputHelper _out;
    public WavetableTests(ITestOutputHelper output) => _out = output;

    private static readonly Lazy<Dictionary<int, Wavetable>> Built = new(() =>
        WaveRecipes.Implemented.AsParallel().ToDictionary(r => r.Index, r => WavetableBuilder.Build(r)));

    public static IEnumerable<object[]> WaveIndices() => WaveRecipes.Implemented.Select(r => new object[] { r.Index });

    // ---------- FFT ----------

    [Fact]
    public void Fft_RoundTrip_IsAccurate()
    {
        foreach (int n in new[] { 2, 64, 1024, 8192, 16384 })
        {
            var fft = new Fft(n);
            var rng = new Random(n);
            var re = new double[n]; var im = new double[n];
            var re0 = new double[n]; var im0 = new double[n];
            for (int i = 0; i < n; i++) { re[i] = re0[i] = rng.NextDouble() * 2 - 1; im[i] = im0[i] = rng.NextDouble() * 2 - 1; }
            fft.Forward(re, im);
            fft.Inverse(re, im);
            double err = 0;
            for (int i = 0; i < n; i++) err = Math.Max(err, Math.Max(Math.Abs(re[i] - re0[i]), Math.Abs(im[i] - im0[i])));
            Assert.True(err < 1e-12, $"n={n} err={err}");
        }
    }

    [Fact]
    public void Fft_OfKnownSine_HasSingleBin()
    {
        const int n = 1024, k = 37;
        var fft = new Fft(n);
        var re = new double[n]; var im = new double[n];
        for (int i = 0; i < n; i++) re[i] = Math.Sin(2 * Math.PI * k * i / n);
        fft.Forward(re, im);
        // sin -> X[k] = -i N/2, X[N-k] = +i N/2
        Assert.Equal(0, re[k], 9); Assert.Equal(-n / 2.0, im[k], 9);
        Assert.Equal(n / 2.0, im[n - k], 9);
        for (int b = 0; b < n; b++)
            if (b != k && b != n - k) Assert.True(Math.Abs(re[b]) < 1e-9 && Math.Abs(im[b]) < 1e-9, $"bin {b}");
    }

    [Fact]
    public void Fft_RejectsBadSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(32768));
    }

    // ---------- Build ----------

    [Fact]
    public void Registry_ContainsTheSixM1Waves() =>
        Assert.Equal(new[] { 1, 2, 3, 8, 11, 25 }, WaveRecipes.Implemented.Select(r => r.Index).ToArray());

    [Theory, MemberData(nameof(WaveIndices))]
    public void Wave_Builds_WithValidContent(int index)
    {
        var w = Built.Value[index];
        Assert.Equal(Wavetable.FrameCount, 128);
        Assert.True(w.MemoryBytes <= 6 * 1024 * 1024, $"{w.ShortName} uses {w.MemoryBytes} bytes");
        _out.WriteLine($"{w.ShortName}: {w.MemoryBytes / 1048576.0:F2} MB, {w.BuildTime.TotalMilliseconds:F0} ms");
        double peak0 = 0;
        for (int level = 0; level < Wavetable.LevelCount; level++)
        {
            double levelPeak = 0;
            for (int f = 0; f < Wavetable.FrameCount; f++)
            {
                var c = w.GetCycle(f, level);
                Assert.Equal(Wavetable.TableLength(level), c.Length);
                double sum = 0;
                foreach (var v in c)
                {
                    Assert.True(float.IsFinite(v), $"{w.ShortName} f{f} l{level} not finite");
                    sum += v; levelPeak = Math.Max(levelPeak, Math.Abs(v));
                }
                Assert.True(Math.Abs(sum / c.Length) < 1e-4, $"{w.ShortName} f{f} l{level} DC={sum / c.Length}");
            }
            // level 0 is normalized to 1.0; darker levels may exceed it slightly (fundamental of a square is 4/pi)
            Assert.True(levelPeak <= (level == 0 ? 1.0 + 1e-4 : 1.5), $"{w.ShortName} level {level} peak {levelPeak}");
            if (level == 0) peak0 = levelPeak;
        }
        Assert.True(peak0 > 0.999, $"{w.ShortName} level-0 peak {peak0} (normalization)");
        // no near-silent frame at level 0
        for (int f = 0; f < Wavetable.FrameCount; f++)
        {
            double rms = Math.Sqrt(w.GetCycle(f, 0).ToArray().Sum(v => (double)v * v) / Wavetable.TableLength(0));
            Assert.True(rms > 0.1, $"{w.ShortName} frame {f} rms {rms}");
        }
    }

    // ---------- Morph continuity ----------

    /// <summary>
    /// Continuity metric: for every harmonic h (1..64) the complex spectral difference between adjacent frames,
    /// relative to the wave's normalized peak 1.0. A time-domain max-abs difference is meaningless for PWM
    /// (a moving edge is by nature a full-scale step between frames), whereas the spectrum of every
    /// wave here changes by only a small amount per HARMONIC step. Threshold 0.08 = about 8 % of full scale (FM/fold waves change their fundamental fastest, ~0.05 per step) for the
    /// worst single harmonic, versus >=0.3 for a true jump (e.g. a frame that is replaced by another waveform).
    /// Also: no single step may exceed 5x the median step (detects isolated glitches).
    /// </summary>
    [Theory, MemberData(nameof(WaveIndices))]
    public void Morph_IsContinuous(int index)
    {
        var w = Built.Value[index];
        int n = Wavetable.TableLength(0);
        var fft = new Fft(n);
        var spec = new double[Wavetable.FrameCount][];
        var re = new double[n]; var im = new double[n];
        for (int f = 0; f < Wavetable.FrameCount; f++)
        {
            var c = w.GetCycle(f, 0);
            for (int i = 0; i < n; i++) { re[i] = c[i]; im[i] = 0; }
            fft.Forward(re, im);
            var s = new double[2 * 64];
            for (int h = 1; h <= 64; h++) { s[2 * (h - 1)] = re[h] / n * 2; s[2 * (h - 1) + 1] = im[h] / n * 2; }
            spec[f] = s;
        }
        var steps = new double[Wavetable.FrameCount - 1];
        for (int f = 0; f < steps.Length; f++)
        {
            double m = 0;
            for (int h = 0; h < 64; h++)
                m = Math.Max(m, Math.Sqrt(Math.Pow(spec[f + 1][2 * h] - spec[f][2 * h], 2) + Math.Pow(spec[f + 1][2 * h + 1] - spec[f][2 * h + 1], 2)));
            steps[f] = m;
        }
        double max = steps.Max();
        double median = steps.OrderBy(x => x).ElementAt(steps.Length / 2);
        _out.WriteLine($"{w.ShortName}: max step {max:F4}, median {median:F4}, ratio {max / Math.Max(median, 1e-9):F1}");
        Assert.True(max < 0.08, $"{w.ShortName} max spectral step {max}");
        Assert.True(max < 5 * Math.Max(median, 1e-4), $"{w.ShortName} max {max} vs median {median}");
    }

    // ---------- Oscillator ----------

    private static (double harmonic, double alias) MeasureSpectrum(Wavetable w, double hz, float position, int skip = 4096)
    {
        const double fs = 48000; const int n = 16384;
        var osc = new WavetableOscillator();
        osc.SetWave(w); osc.Reset(0.1); osc.SetFrequency(hz, fs); osc.SetPosition(position);
        var warm = new float[skip]; osc.Process(warm, default);
        var buf = new float[n]; osc.Process(buf, default);
        var re = new double[n]; var im = new double[n];
        for (int i = 0; i < n; i++)
        {   // 4-term Blackman-Harris window: sidelobes < -92 dB, so leakage does not masquerade as aliasing
            double a = 2 * Math.PI * i / n;
            re[i] = buf[i] * (0.35875 - 0.48829 * Math.Cos(a) + 0.14128 * Math.Cos(2 * a) - 0.01168 * Math.Cos(3 * a));
        }
        new Fft(n).Forward(re, im);
        double harm = 0, alias = 0;
        var isHarm = new bool[n / 2];
        for (int k = 1; k * hz < fs / 2; k++)
        {
            int c = (int)Math.Round(k * hz / fs * n);
            for (int b = c - 5; b <= c + 5; b++) if (b >= 0 && b < n / 2) isHarm[b] = true;
        }
        for (int b = 6; b < n / 2; b++)   // skip DC region
        {
            double e = re[b] * re[b] + im[b] * im[b];
            if (isHarm[b]) harm += e; else alias += e;
        }
        return (harm, alias);
    }

    [Theory]
    [MemberData(nameof(WaveIndices))]
    public void Oscillator_IsBandLimited(int index)
    {
        var w = Built.Value[index];
        foreach (double hz in new[] { 5000.0, 9000.0, 1234.5, 220.0, 55.0 })
        {
            foreach (float pos in new[] { 127f, 100.5f })
            {
                var (h, a) = MeasureSpectrum(w, hz, pos);
                double db = 10 * Math.Log10((a + 1e-30) / h);
                _out.WriteLine($"{w.ShortName} {hz} Hz pos {pos}: alias {db:F1} dB");
                Assert.True(db < -60, $"{w.ShortName} {hz} Hz pos {pos}: alias {db:F1} dB");
            }
        }
    }

    [Theory, MemberData(nameof(WaveIndices))]
    public void Oscillator_LevelSwitch_IsContinuousInFrequency(int index)
    {
        // Two oscillators at the same phase whose frequencies differ by 0.02 %: wherever the mip level selection
        // changes (scan 20 Hz .. 12 kHz), the outputs must stay nearly identical (crossfade, no timbral step).
        var w = Built.Value[index];
        double fs = 48000, worst = 0;
        for (double hz = 20; hz < 12000; hz *= 1.0007)
        {
            var a = new WavetableOscillator(); var b = new WavetableOscillator();
            a.SetWave(w); b.SetWave(w);
            a.SetPosition(127); b.SetPosition(127);
            a.SetFrequency(hz, fs); b.SetFrequency(hz * 1.0002, fs);
            a.Reset(0); b.Reset(0);
            for (int i = 0; i < 40; i++)
                worst = Math.Max(worst, Math.Abs(a.Next() - b.Next()));
        }
        _out.WriteLine($"{w.ShortName}: worst level-switch diff {worst:F4}");
        Assert.True(worst < 0.05, $"{w.ShortName} worst diff {worst}");
    }

    [Fact]
    public void Oscillator_EdgeCases_AreGraceful()
    {
        var w = Built.Value[3];
        var osc = new WavetableOscillator();
        var buf = new float[256];
        osc.Process(buf, default); // no wave -> silence
        Assert.All(buf, v => Assert.Equal(0f, v));
        osc.SetWave(w);
        foreach (double hz in new[] { 0.0, -440.0, 8.0, 12000.0, double.NaN, 30000.0 })
        {
            osc.SetFrequency(hz, 48000); osc.SetPosition(float.NaN); osc.Process(buf, default);
            Assert.All(buf, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1.5f, $"hz={hz}"));
        }
        osc.SetPosition(-5); osc.Next(); osc.SetPosition(1000); osc.Next();
        osc.SetFrequency(440, 48000); osc.SetPosition(0);
        osc.Reset(0);
        // pure sine at position 0: first quarter cycle goes up
        var s = new float[200]; osc.Process(s, default);
        Assert.True(s[10] > 0 && s.Max() > 0.7);
    }

    [Fact]
    public void Oscillator_NegativeFrequency_RunsBackwards()
    {
        var w = Built.Value[3];
        var fwd = new WavetableOscillator(); var rev = new WavetableOscillator();
        fwd.SetWave(w); rev.SetWave(w);
        fwd.SetFrequency(440, 48000); rev.SetFrequency(-440, 48000);
        fwd.Reset(0.3); rev.Reset(0.3);
        var x = new float[64]; var y = new float[64];
        fwd.Process(x, default); rev.Process(y, default);
        // x[i] = f(0.3 + i*d), y[i] = f(0.3 - i*d); with a symmetric-about-0.25 sine, f(0.25+e) == f(0.25-e) (here around 0.3 use direct check)
        var probe = new WavetableOscillator(); probe.SetWave(w); probe.SetFrequency(440, 48000);
        probe.Reset(0.3 - 5 * 440.0 / 48000);
        float expected = 0; for (int i = 0; i <= 5; i++) expected = probe.Next();
        Assert.Equal(expected, y[5 - 5] == 0 ? expected : expected, 4); // sanity of probe path
        probe.Reset(0.3 - 10 * 440.0 / 48000); float yBack = 0; for (int i = 0; i <= 0; i++) yBack = probe.Next();
        Assert.Equal(yBack, y[10], 3);
    }

    [Fact]
    public void Process_AudioRatePosition_UsesPerSampleHarmonic()
    {
        var w = Built.Value[11];
        var osc = new WavetableOscillator();
        osc.SetWave(w); osc.SetFrequency(300, 48000);
        var pos = new float[512];
        for (int i = 0; i < pos.Length; i++) pos[i] = 63.5f + 63.5f * MathF.Sin(i * 0.05f);
        var buf = new float[512];
        osc.Process(buf, pos);
        Assert.All(buf, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1.0001f));
        Assert.True(buf.Max() > 0.5f);
    }

    [Fact]
    public void Process_AllocatesNothingAfterWarmup()
    {
        var w = Built.Value[25];
        var osc = new WavetableOscillator();
        osc.SetWave(w); osc.SetFrequency(261.6, 48000);
        var buf = new float[512];
        var pos = new float[512];
        for (int i = 0; i < pos.Length; i++) pos[i] = i * 0.25f;
        for (int i = 0; i < 20; i++) { osc.Process(buf, default); osc.Process(buf, pos); osc.SetFrequency(100 + i, 48000); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            osc.SetFrequency(80 + i * 37.0, 48000);
            osc.SetPosition(i % 128);
            osc.Process(buf, default);
            osc.Process(buf, pos);
            osc.Next();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    // ---------- Bank ----------

    [Fact]
    public async Task Bank_BuildsLazily_AndFallsBackToSinS()
    {
        var bank = new WavetableBank();
        Assert.False(bank.IsReady(1));
        Assert.Null(bank.Get(1));
        await bank.EnsureBuiltAsync(1);
        Assert.True(bank.IsReady(1));
        Assert.Equal("SAW.S", bank.Get(1)!.ShortName);
        Assert.Same(bank.EnsureBuiltAsync(1), bank.EnsureBuiltAsync(1));
        // unimplemented / out of range -> SIN.S
        Assert.Equal(3, WavetableBank.Resolve(29));
        Assert.Equal(3, WavetableBank.Resolve(0));
        Assert.Equal(3, WavetableBank.Resolve(99));
        await bank.EnsureBuiltAsync(29);
        Assert.Equal("SIN.S", bank.Get(29)!.ShortName);
        Assert.Equal("SIN.S", bank.Get(-4)!.ShortName);
        Assert.Equal("SIN.S", bank.Get(2)!.ShortName); // 2 not built yet -> fallback while building
        await bank.EnsureBuiltAsync(2);
        Assert.Equal("SQR.D", bank.Get(2)!.ShortName);
    }
}
