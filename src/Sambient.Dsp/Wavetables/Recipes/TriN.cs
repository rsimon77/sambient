namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Triangle plus a noise spectrum: 1024 random-phase partials whose spectral shape moves from pink (1/sqrt(h) amplitude)
/// to white (flat) while their level rises from nothing to ~1.6x the triangle's RMS. Each partial's phase drifts slowly
/// with HARMONIC, so neighbouring frames are similar. Frame 0 is a pure triangle.
/// </summary>
public sealed class TriN : IWaveRecipe
{
    public int Index => 31;
    public string ShortName => "TRI.N";
    public string Description => "Triangle with a pink-to-white noise spectrum fading in";

    private const int H = 1024;

    private static readonly Lazy<(double[] phase, double[] drift)> Rnd = new(() =>
    {
        var rng = new Random(3101);
        var p = new double[H + 1]; var d = new double[H + 1];
        for (int h = 1; h <= H; h++) { p[h] = rng.NextDouble() * RecipeUtil.TwoPi; d[h] = RecipeUtil.Gauss(rng) * 1.2; }
        return (p, d);
    });

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        var (p0, drift) = Rnd.Value;
        double level = 1.6 * 0.577 * Math.Pow(u, 1.3);
        double white = RecipeUtil.Smooth(1.15 * u);
        double kPink = Math.Sqrt(2.0 / 7.5), kWhite = Math.Sqrt(2.0 / H);
        var amp = new double[H + 1]; var ph = new double[H + 1];
        for (int h = 1; h <= H; h++)
        {
            double noise = level * ((1.0 - white) * kPink / Math.Sqrt(h) + white * kWhite);
            double tri = RecipeUtil.SineCoef(1, h);
            double ph0 = p0[h] + drift[h] * u;
            // triangle (sine phase, coefficient c) + noise partial (amplitude noise, phase ph0) summed as sine/cosine parts
            double sc = tri + noise * Math.Cos(ph0), cc = noise * Math.Sin(ph0);
            amp[h] = Math.Sqrt(sc * sc + cc * cc); ph[h] = Math.Atan2(cc, sc);
        }
        RecipeUtil.AdditivePhased(amp, ph, cycle);
        RecipeUtil.SoftLimit(cycle, 2.0, RecipeUtil.Smooth(3.0 * u));   // noise crest factor ~4 -> ~2, keeps the loudness near the tonal frames
        RecipeUtil.Finish(cycle);
    }
}
