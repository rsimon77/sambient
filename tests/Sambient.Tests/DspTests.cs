using Sambient.Dsp;
using Sambient.Engine;

namespace Sambient.Tests;

public class DspTests
{
    [Theory]
    [InlineData(55.0)]
    [InlineData(220.0)]
    [InlineData(440.0)]
    public void SineOscillator_HasExpectedFrequencyAndAmplitude(double hz)
    {
        const int sr = 48000;
        var osc = new SineOscillator(hz, sr);
        var buf = new float[sr];
        osc.Process(buf);

        int crossings = 0;
        float max = 0;
        for (int i = 1; i < buf.Length; i++)
        {
            if (buf[i - 1] < 0 && buf[i] >= 0) crossings++; // rising zero crossings = cycles
            max = Math.Max(max, Math.Abs(buf[i]));
        }
        Assert.InRange(crossings, hz - 1, hz + 1);
        Assert.True(max <= 1f);
    }

    [Fact]
    public void TestToneEngine_DisabledIsExactSilence()
    {
        var engine = new TestToneEngine(48000);
        var buf = new float[512];
        Array.Fill(buf, 1f);
        engine.Render(buf, 256);
        Assert.All(buf, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void TestToneEngine_EnabledProducesSignalWithoutNaN()
    {
        var engine = new TestToneEngine(48000) { Enabled = true };
        var buf = new float[512];
        float peak = 0;
        for (int b = 0; b < 8; b++)
        {
            engine.Render(buf, 256);
            foreach (var s in buf)
            {
                Assert.False(float.IsNaN(s) || float.IsInfinity(s));
                peak = Math.Max(peak, Math.Abs(s));
            }
        }
        Assert.InRange(peak, 0.01f, 1f);
    }

    [Fact]
    public void TestToneEngine_ToggleOffReturnsToExactSilence()
    {
        var engine = new TestToneEngine(48000) { Enabled = true };
        var buf = new float[512];
        engine.Render(buf, 256);
        engine.Enabled = false;
        engine.Render(buf, 256); // ramp down
        engine.Render(buf, 256);
        Assert.All(buf, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void TestToneEngine_RenderAllocatesNothing()
    {
        var engine = new TestToneEngine(48000) { Enabled = true };
        var buf = new float[512];
        engine.Render(buf, 256); // warm-up (JIT)
        engine.Render(buf, 256);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) engine.Render(buf, 256);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }
}
