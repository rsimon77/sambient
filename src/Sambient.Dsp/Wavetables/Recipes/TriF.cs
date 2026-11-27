namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Triangle through a triangular wave-folder, gain 1 -> 6.5, with a mid-range offset adding even harmonics. Piecewise-linear folding keeps the spectrum soft (1/h^2).</summary>
public sealed class TriF : IWaveRecipe
{
    public int Index => 12;
    public string ShortName => "TRI.F";
    public string Description => "Wave-folded triangle: soft zig-zag folds, gain 1 to 6.5";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double g = 1.0 + 5.5 * u;
        double b = 0.5 * Math.Sin(Math.PI * u);
        RecipeUtil.Render(cycle, t => RecipeUtil.FoldTri(g * RecipeUtil.Tri(t) + b));
        RecipeUtil.Finish(cycle);
    }
}
