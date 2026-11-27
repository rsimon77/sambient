namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Additive spectrum morphing between three templates (odd 1/h^2 triangle -> full 1/h saw -> sparse Fibonacci-partial "bell"),
/// then wave-folded with a gain that rises 1 -> 2.8.
/// </summary>
public sealed class AdMp : IWaveRecipe
{
    public int Index => 26;
    public string ShortName => "AD.MP";
    public string Description => "Additive overtone morph (triangle, saw, bell partials) with wave-folding";

    private static readonly int[] Fib = [1, 2, 3, 5, 8, 13, 21, 34, 55, 89];

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double s = 2.0 * u;
        double w12 = RecipeUtil.Smooth(s), w23 = RecipeUtil.Smooth(s - 1.0);
        var t1 = new double[200]; var t2 = new double[200]; var t3 = new double[200];
        for (int h = 1; h < 200; h++)
        {
            if ((h & 1) == 1) t1[h] = (((h >> 1) & 1) == 0 ? 1.0 : -1.0) / ((double)h * h);
            t2[h] = 1.0 / h / (1.0 + Math.Pow(h / 40.0, 2));
        }
        foreach (int h in Fib) t3[h] = Math.Pow(h, -0.6);
        var amp = new double[200];
        for (int h = 1; h < 200; h++)
        {
            double a = (1.0 - w12) * t1[h] * 1.0 + w12 * t2[h];
            amp[h] = (1.0 - w23) * a + w23 * t3[h];
        }
        RecipeUtil.AdditiveSines(amp, cycle);
        RecipeUtil.Finish(cycle);
        double g = 1.0 + 1.8 * u;
        for (int i = 0; i < cycle.Length; i++) cycle[i] = (float)Math.Sin(0.5 * Math.PI * g * cycle[i]);
        RecipeUtil.Finish(cycle);
    }
}
