namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Wave-folding sine: y = sin(pi/2 * g * sin(t)), fold gain g = 1 -> 6.</summary>
public sealed class SinF : IWaveRecipe
{
    public int Index => 11;
    public string ShortName => "SIN.F";
    public string Description => "Wave-folded sine, fold amount 1 to 6";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double g = 1.0 + 5.0 * RecipeUtil.U(frame);
        int n = cycle.Length;
        for (int i = 0; i < n; i++)
            cycle[i] = (float)Math.Sin(0.5 * Math.PI * g * Math.Sin(2.0 * Math.PI * i / n));
    }
}
