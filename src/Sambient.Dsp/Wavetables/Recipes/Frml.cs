namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Sine through a triangular wave-folder (gain 1 -> 4.2) followed by a hard clipper whose gain rises 1 -> 2.6.</summary>
public sealed class Frml : IWaveRecipe
{
    public int Index => 20;
    public string ShortName => "FRML";
    public string Description => "Wave-folded then clipped sine: folds with flattened, clipped peaks";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double fold = 1.0 + 3.2 * u, clip = 1.0 + 1.6 * u;
        RecipeUtil.Render(cycle, t => RecipeUtil.Clip(clip * RecipeUtil.FoldTri(fold * Math.Sin(RecipeUtil.TwoPi * t))));
        RecipeUtil.Finish(cycle);
    }
}
