namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Triangle through a tanh shaper (drive 0.5 -> 7) with a mid-range bias that adds even harmonics: triangle -> rounded -> trapezoid/square.</summary>
public sealed class TriS : IWaveRecipe
{
    public int Index => 6;
    public string ShortName => "TRI.S";
    public string Description => "Wave-shaped triangle: soft triangle to a rounded trapezoid, with asymmetric mid-range";

    private static double[] Knots = [0.0, 0.076, 0.132, 0.186, 0.243, 0.306, 0.374, 0.442, 0.505, 0.563, 0.617, 0.668, 0.718, 0.769, 0.824, 0.888, 1.0];   // path-equalizing HARMONIC warp (generated; see RecipeUtil.Warp)

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.Warp(RecipeUtil.U(frame), Knots);
        double g = 0.3 + 6.7 * u;
        double bias = 0.55 * u * (1.0 - u) * 2.0 * (1.0 - u);
        RecipeUtil.Render(cycle, t => Math.Tanh(g * (RecipeUtil.Tri(t) + bias)));
        RecipeUtil.Finish(cycle);
    }
}
