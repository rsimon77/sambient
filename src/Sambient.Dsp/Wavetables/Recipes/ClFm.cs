namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>A sine, FM-modulated (modulator at 2x, index 0 -> 3), then digitally hard-clipped with gain 1 -> 4.2.</summary>
public sealed class ClFm : IWaveRecipe
{
    public int Index => 19;
    public string ShortName => "CL.FM";
    public string Description => "FM modulating a digitally clipped sine: clean sine to clipped FM square-ish tones";

    private static double[] Knots = [0.0, 0.047, 0.109, 0.208, 0.402, 0.678, 0.717, 0.749, 0.776, 0.802, 0.825, 0.848, 0.871, 0.898, 0.929, 0.963, 1.0];   // path-equalizing HARMONIC warp (generated; see RecipeUtil.Warp)

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.Warp(RecipeUtil.U(frame), Knots);
        double idx = 3.0 * u, gain = 1.0 + 3.2 * u;
        RecipeUtil.Render(cycle, t =>
        {
            double ph = RecipeUtil.TwoPi * t;
            return RecipeUtil.Clip(gain * Math.Sin(ph + idx * Math.Sin(2.0 * ph)));
        });
        RecipeUtil.Finish(cycle);
    }
}
