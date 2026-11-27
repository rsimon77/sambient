namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Bit-crushed sine: amplitude quantized to a continuously variable number of levels, 20 -> 0.7 per unit (a square at the end) (log sweep).</summary>
public sealed class SinC : IWaveRecipe
{
    public int Index => 13;
    public string ShortName => "SIN.C";
    public string Description => "Bit-crushed sine: fine quantization down to a 1-bit square";

    private static double[] Knots = [0.0, 0.084, 0.149, 0.215, 0.278, 0.34, 0.4, 0.462, 0.524, 0.581, 0.643, 0.697, 0.763, 0.826, 0.887, 0.934, 1.0];   // path-equalizing HARMONIC warp (generated; see RecipeUtil.Warp)

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double levels = RecipeUtil.Exp(20.0, 0.7, RecipeUtil.Warp(RecipeUtil.U(frame), Knots));
        RecipeUtil.Render(cycle, t => RecipeUtil.Crush(Math.Sin(RecipeUtil.TwoPi * t), levels));
        RecipeUtil.Finish(cycle);
    }
}
