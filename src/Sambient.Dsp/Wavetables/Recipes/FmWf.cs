namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Triangle phase-modulated by a sine at 2x (index 0 -> 2.4 rad) and then folded by a triangular wave-folder, gain 1 -> 4.5.</summary>
public sealed class FmWf : IWaveRecipe
{
    public int Index => 24;
    public string ShortName => "FM.WF";
    public string Description => "FM-modulated triangle through a wave-folder";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double idx = 2.4 * u / RecipeUtil.TwoPi, g = 1.0 + 3.5 * u;
        RecipeUtil.Render(cycle, t => RecipeUtil.FoldTri(g * RecipeUtil.Tri(t + idx * Math.Sin(RecipeUtil.TwoPi * 2.0 * t))));
        RecipeUtil.Finish(cycle);
    }
}
