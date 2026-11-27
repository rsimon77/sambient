namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>FM sine (modulator at 2x, index 0 -> 5) that is bit-crushed progressively (24 -> 1.2 levels).</summary>
public sealed class FmCr : IWaveRecipe
{
    public int Index => 14;
    public string ShortName => "FM.CR";
    public string Description => "Bit-crushed FM sine: modulation index and quantization both increase";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double idx = 5.0 * u, levels = RecipeUtil.Exp(24.0, 1.2, u);
        RecipeUtil.Render(cycle, t =>
        {
            double ph = RecipeUtil.TwoPi * t;
            return RecipeUtil.Crush(Math.Sin(ph + idx * Math.Sin(2.0 * ph)), levels);
        });
        RecipeUtil.Finish(cycle);
    }
}
