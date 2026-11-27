namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Additive 1/h spectrum morphing from even-harmonics-only to odd-harmonics-only (equal-power crossfade).</summary>
public sealed class EvOd : IWaveRecipe
{
    public int Index => 8;
    public string ShortName => "EV.OD";
    public string Description => "Even-harmonics-only to odd-harmonics-only spectrum morph";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double p = RecipeUtil.U(frame) * Math.PI * 0.5;
        double we = Math.Cos(p), wo = Math.Sin(p);
        var amp = new double[1025];
        for (int h = 1; h < amp.Length; h++)
            amp[h] = ((h & 1) == 0 ? we : wo) / h;
        RecipeUtil.AdditiveSines(amp, cycle);
    }
}
