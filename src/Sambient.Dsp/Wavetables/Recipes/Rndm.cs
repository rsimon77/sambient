namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// 127 individual random waveforms (frame 0 stays a plain sine as the anchor). Every frame has its own fixed seed and
/// is either a random additive spectrum (sparse partials with a random tilt and bandwidth) or a smooth random
/// break-point curve. There is deliberately no continuity between neighbouring frames.
/// </summary>
public sealed class Rndm : IWaveRecipe
{
    public int Index => 29;
    public string ShortName => "RNDM";
    public string Description => "127 individual random waveforms (frame 0 = sine)";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        int n = cycle.Length;
        if (frame == 0)
        {
            for (int i = 0; i < n; i++) cycle[i] = (float)Math.Sin(RecipeUtil.TwoPi * i / n);
            return;
        }
        var rng = new Random(2900 + frame * 7919);
        if (rng.NextDouble() < 0.6)
        {
            int maxH = 6 + rng.Next(60);
            int count = 2 + rng.Next(18);
            double tilt = 0.3 + 1.9 * rng.NextDouble();
            var amp = new double[maxH + 1]; var ph = new double[maxH + 1];
            for (int k = 0; k < count; k++)
            {
                int h = 1 + rng.Next(maxH);
                amp[h] += Math.Pow(h, -tilt) * (0.3 + rng.NextDouble());
            }
            for (int h = 1; h <= maxH; h++) ph[h] = rng.NextDouble() * RecipeUtil.TwoPi;
            amp[1] = Math.Max(amp[1], 0.35 * amp.Max());   // keep the pitch anchored on the fundamental
            if (amp.Max() < 1e-9) amp[1] = 1;
            RecipeUtil.AdditivePhased(amp, ph, cycle);
        }
        else
        {
            int pts = 3 + rng.Next(14);
            var y = new float[pts];
            for (int k = 0; k < pts; k++) y[k] = (float)(2.0 * rng.NextDouble() - 1.0);
            for (int i = 0; i < n; i++)
            {
                double pos = (double)i / n * pts;
                int a = (int)pos; double f = pos - a;
                double y0 = y[(a - 1 + pts) % pts], y1 = y[a % pts], y2 = y[(a + 1) % pts], y3 = y[(a + 2) % pts];
                double c1 = 0.5 * (y2 - y0), c2 = y0 - 2.5 * y1 + 2 * y2 - 0.5 * y3, c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
                cycle[i] = (float)(((c3 * f + c2) * f + c1) * f + y1);
            }
        }
        RecipeUtil.Finish(cycle);
    }
}
