using Sambient.Dsp.Envelopes;
using Sambient.Dsp.Filters;
using Sambient.Dsp.Params;

namespace Sambient.Tests;

public class FilterEnvelopeTests
{
    private const float Sr = 48000f;

    // ---- ParamCurves ----
    [Fact]
    public void Times_Endpoints()
    {
        Assert.Equal(0f, ParamCurves.AttackMs(0), 3);
        Assert.Equal(10000f, ParamCurves.AttackMs(127), 1);
        Assert.Equal(10000f, ParamCurves.ReleaseMs(127, false), 1);
        Assert.Equal(20000f, ParamCurves.ReleaseMs(127, true), 1);
        Assert.InRange(ParamCurves.AttackMs(64), 600f, 1000f);
    }

    [Fact]
    public void Cutoff_And_Q_And_Bandwidth()
    {
        Assert.Equal(70f, ParamCurves.CutoffHz(0), 2);
        Assert.Equal(14400f, ParamCurves.CutoffHz(127), 0);
        Assert.Equal(70f * MathF.Sqrt(14400f / 70f), ParamCurves.CutoffHz(63.5f), 1);
        Assert.Equal(0.3f, ParamCurves.ResonanceQ(0), 4);
        Assert.Equal(10f, ParamCurves.ResonanceQ(127), 3);
        Assert.Equal(3.3f, ParamCurves.BandpassOctaves(0), 3);
        Assert.Equal(0.5f, ParamCurves.BandpassOctaves(127), 3);
        for (float v = 0; v <= 127; v += 7.3f)
            Assert.Equal(v, ParamCurves.CutoffToKnob(ParamCurves.CutoffHz(v)), 2);
    }

    [Fact]
    public void Level_Pan_Glide_Detune()
    {
        Assert.Equal(0f, ParamCurves.LevelGain(0));
        Assert.Equal(1f, ParamCurves.LevelGain(63.5f), 4);
        Assert.Equal(2f, ParamCurves.LevelGain(127), 2); // +6 dB
        Assert.Equal(0.001f, ParamCurves.LevelGain(1f), 3);
        var (l, r) = ParamCurves.PanGains(0);
        Assert.Equal(l, r, 5);
        Assert.Equal(1f, l * l + r * r, 5);
        var (l2, r2) = ParamCurves.PanGains(-63);
        Assert.Equal(1f, l2, 5); Assert.Equal(0f, r2, 5);
        var (l3, r3) = ParamCurves.PanGains(63);
        Assert.Equal(0f, l3, 5); Assert.Equal(1f, r3, 5);
        Assert.Equal(10000f, ParamCurves.GlideMs(127), 1);
        Assert.Equal(20f, ParamCurves.UnisonDetuneCents(127), 4);
        Assert.Equal(0f, ParamCurves.UnisonDetuneCents(0), 4);
    }

    [Fact]
    public void Curves_AreMonotonic()
    {
        Func<float, float>[] fs =
        {
            ParamCurves.AttackMs, v => ParamCurves.ReleaseMs(v, true), ParamCurves.CutoffHz,
            ParamCurves.ResonanceQ, ParamCurves.LevelGain, ParamCurves.GlideMs,
            ParamCurves.UnisonDetuneCents, v => ParamCurves.PanGains(v - 63).r,
        };
        foreach (var f in fs)
            for (float v = 0.5f; v <= 127; v += 0.5f)
                Assert.True(f(v) >= f(v - 0.5f), $"not monotonic at {v}");
        for (float v = 0.5f; v <= 127; v += 0.5f)
            Assert.True(ParamCurves.BandpassOctaves(v) <= ParamCurves.BandpassOctaves(v - 0.5f));
    }

    [Fact]
    public void SmoothedValue_RampsLinearly()
    {
        var s = new SmoothedValue(0f, 1000, 10f); // 10 samples
        s.SetTarget(1f);
        Assert.True(s.IsSmoothing);
        Assert.Equal(0.1f, s.Next(), 5);
        s.Skip(4);
        Assert.Equal(0.5f, s.Current, 5);
        for (int i = 0; i < 5; i++) s.Next();
        Assert.False(s.IsSmoothing);
        Assert.Equal(1f, s.Current);
    }

    // ---- SVF ----
    private static float GainDb(FilterMode mode, float cutoff, float q, float freq)
    {
        var f = new StateVariableFilter(Sr, mode);
        f.SetParameters(cutoff, q);
        int n = (int)Sr;
        double sumIn = 0, sumOut = 0;
        for (int i = 0; i < n; i++)
        {
            float x = MathF.Sin(2 * MathF.PI * freq * i / Sr);
            float y = f.Process(x);
            if (i > n / 2) { sumIn += x * x; sumOut += y * y; }
        }
        return (float)(10 * Math.Log10(sumOut / sumIn));
    }

    [Fact]
    public void Lowpass_Response()
    {
        Assert.InRange(GainDb(FilterMode.LowPass, 1000, 0.707f, 100), -0.5f, 0.5f);
        Assert.True(GainDb(FilterMode.LowPass, 1000, 0.707f, 8000) < -30f);
    }

    [Fact]
    public void Highpass_Response()
    {
        Assert.InRange(GainDb(FilterMode.HighPass, 1000, 0.707f, 8000), -0.5f, 0.5f);
        Assert.True(GainDb(FilterMode.HighPass, 1000, 0.707f, 100) < -30f);
    }

    [Fact]
    public void Bandpass_UnityAtCenter()
    {
        Assert.InRange(GainDb(FilterMode.BandPass, 1000, 2f, 1000), -0.2f, 0.2f);
        Assert.True(GainDb(FilterMode.BandPass, 1000, 2f, 100) < -15f);
        float q = StateVariableFilter.OctavesToQ(1f);
        Assert.Equal(1.414f, q, 2);
    }

    [Fact]
    public void Svf_StableUnderFastModulation()
    {
        var f = new StateVariableFilter(Sr, FilterMode.LowPass);
        var rng = new Random(1);
        float maxAbs = 0;
        for (int i = 0; i < (int)Sr * 10; i++)
        {
            f.SetParameters(20f + (float)rng.NextDouble() * 20000f, 10f);
            float y = f.Process((float)(rng.NextDouble() * 2 - 1));
            Assert.True(float.IsFinite(y));
            maxAbs = Math.Max(maxAbs, MathF.Abs(y));
        }
        Assert.True(maxAbs < 100f, $"max {maxAbs}");
    }

    [Fact]
    public void Svf_OffIsBitExact()
    {
        var f = new StateVariableFilter(Sr, FilterMode.Off);
        var rng = new Random(2);
        for (int i = 0; i < 1000; i++)
        {
            float x = (float)rng.NextDouble();
            Assert.Equal(BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(f.Process(x)));
        }
    }

    // ---- Envelope ----
    [Fact]
    public void Attack_TimeAccuracy()
    {
        foreach (float ms in new[] { 50f, 500f, 2000f })
        {
            var e = new ArEnvelope(Sr, ms, 100f);
            e.NoteOn();
            int n = 0;
            while (e.Next() < 0.99f) n++;
            float t = n / Sr * 1000f;
            Assert.InRange(t, ms * 0.8f, ms * 1.2f);
        }
    }

    [Fact]
    public void Attack_HoldsAtOne_ReleaseGoesIdle()
    {
        var e = new ArEnvelope(Sr, 5f, 100f);
        e.NoteOn();
        for (int i = 0; i < 4800; i++) e.Next();
        Assert.Equal(1f, e.Next());
        e.NoteOff();
        int n = 0;
        while (e.IsActive && n < Sr * 5) { e.Next(); n++; }
        Assert.False(e.IsActive);
        Assert.Equal(0f, e.Next());
        Assert.InRange(n / Sr * 1000f, 100f, 250f); // -60 dB at 100 ms, idle at -100 dB
    }

    [Fact]
    public void Retrigger_StartsFromCurrentLevel()
    {
        var e = new ArEnvelope(Sr, 100f, 1000f);
        e.NoteOn();
        for (int i = 0; i < 20000; i++) e.Next();
        e.NoteOff();
        float prev = 0;
        for (int i = 0; i < 5000; i++) prev = e.Next();
        e.NoteOn();
        float next = e.Next();
        Assert.True(MathF.Abs(next - prev) < 0.01f);
        Assert.True(next > prev);
    }

    [Fact]
    public void Kill_FinishesQuickly()
    {
        var e = new ArEnvelope(Sr, 1f, 5000f);
        e.NoteOn();
        for (int i = 0; i < 2000; i++) e.Next();
        e.Kill();
        int n = 0;
        while (e.IsActive) { e.Next(); n++; }
        Assert.True(n / Sr * 1000f <= 10f, $"{n / Sr * 1000f} ms");
    }

    [Fact]
    public void Process_DoesNotAllocate()
    {
        var f = new StateVariableFilter(Sr, FilterMode.LowPass);
        var e = new ArEnvelope(Sr, 10f, 100f);
        var buf = new float[512];
        var gain = new float[512];
        e.NoteOn();
        f.Process(buf); e.Process(gain);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            f.SetParameters(500f + i, 2f);
            f.Process(buf);
            e.Process(gain);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
