namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Square wave amplitude-modulated by random noise: y = sq(t) * (1 + d n(t)), depth d = 0 -> 0.95. The noise is a fixed
/// pink-ish random-phase spectrum (harmonics 1..300) that is rotated smoothly between two independent noise cycles
/// (constant power), so the texture drifts continuously with HARMONIC instead of jumping.
/// </summary>
public sealed class SqrR : IWaveRecipe
{
    public int Index => 28;
    public string ShortName => "SQR.R";
    public string Description => "Square wave amplitude-modulated by random noise";

    private static readonly Lazy<float[][]> Noise = new(() =>
    {
        var rng = new Random(2801);
        var res = new float[2][];
        for (int k = 0; k < 2; k++)
        {
            var amp = new double[301]; var ph = new double[301];
            for (int h = 1; h < amp.Length; h++) { amp[h] = Math.Pow(h, -0.5); ph[h] = rng.NextDouble() * RecipeUtil.TwoPi; }
            var c = new float[Wavetable.CycleResolution];
            RecipeUtil.AdditivePhased(amp, ph, c);
            double ms = 0; foreach (var v in c) ms += v * v;
            float g = (float)(1.0 / Math.Sqrt(ms / c.Length));
            for (int i = 0; i < c.Length; i++) c[i] *= g;
            res[k] = c;
        }
        return res;
    });

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double depth = 0.95 * u, th = RecipeUtil.TwoPi * 2.5 * u;
        double ca = Math.Cos(th), sb = Math.Sin(th);
        var nz = Noise.Value;
        RecipeUtil.RenderOversampled(cycle, 4, t => t < 0.5 ? 1.0 : -1.0);
        for (int i = 0; i < cycle.Length; i++)
            cycle[i] *= (float)(1.0 + depth * (ca * nz[0][i] + sb * nz[1][i]));
        RecipeUtil.SoftLimit(cycle, 2.2, RecipeUtil.Smooth(2.0 * u));
        RecipeUtil.Finish(cycle);
    }
}
