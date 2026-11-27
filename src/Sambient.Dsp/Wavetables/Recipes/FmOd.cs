namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>FM sine (modulator at 2x, index 0 -> 3) pushed through a tanh overdrive whose drive rises 0.6 -> 7.</summary>
public sealed class FmOd : IWaveRecipe
{
    public int Index => 15;
    public string ShortName => "FM.OD";
    public string Description => "FM sine morphing into overdrive: clean FM to a saturated, dense wave";

    private static double[] Knots = [0.0, 0.072, 0.168, 0.344, 0.679, 0.734, 0.759, 0.779, 0.797, 0.815, 0.835, 0.857, 0.881, 0.908, 0.936, 0.967, 1.0];   // path-equalizing HARMONIC warp (generated; see RecipeUtil.Warp)

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.Warp(RecipeUtil.U(frame), Knots);
        double idx = 3.0 * u, drive = 0.6 + 6.4 * u;
        RecipeUtil.Render(cycle, t =>
        {
            double ph = RecipeUtil.TwoPi * t;
            return Math.Tanh(drive * Math.Sin(ph + idx * Math.Sin(2.0 * ph)));
        });
        RecipeUtil.Finish(cycle);
    }
}
