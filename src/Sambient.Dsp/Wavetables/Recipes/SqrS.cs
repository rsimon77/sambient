namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Power-law wave-shaper on a sine: y = sgn(s)|s|^p, p = 1 -> 0.05 (sine to a hard square, odd harmonics only).</summary>
public sealed class SqrS : IWaveRecipe
{
    public int Index => 5;
    public string ShortName => "SQR.S";
    public string Description => "Wave-shaped square: sine squared off progressively into a full square";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double p = Math.Pow(2.0, -4.32 * Math.Pow(RecipeUtil.U(frame), 1.35));
        RecipeUtil.Render(cycle, t =>
        {
            double s = Math.Sin(RecipeUtil.TwoPi * t);
            return Math.Sign(s) * Math.Pow(Math.Abs(s), p);
        });
        RecipeUtil.Finish(cycle);
    }
}
