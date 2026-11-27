namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// West-coast complex oscillator in the spirit of the Buchla 259: a principal sine, FM-modulated by a 2x modulator (index 0 -> 1.8),
/// mixed with the modulator itself, then driven through two cascaded sine wave-folders (gain 1 -> 4.2, then x1.4) with a
/// symmetry offset that breathes during the sweep (even harmonics).
/// </summary>
public sealed class Bchl : IWaveRecipe
{
    public int Index => 27;
    public string ShortName => "BCHL";
    public string Description => "West-coast complex oscillator: FM plus cascaded wave-folding, Buchla-style";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double idx = 1.8 * Math.Pow(u, 0.8), g = 1.0 + 3.2 * Math.Pow(u, 0.8), mix = 0.35 * u;
        double sym = 0.45 * u * Math.Sin(RecipeUtil.TwoPi * 1.5 * u);
        RecipeUtil.Render(cycle, t =>
        {
            double ph = RecipeUtil.TwoPi * t;
            double x = 0.8 * Math.Sin(ph + idx * Math.Sin(2.0 * ph)) + mix * Math.Sin(2.0 * ph);
            double y = Math.Sin(0.5 * Math.PI * (g * x + sym));
            return Math.Sin(0.5 * Math.PI * 1.4 * y);
        });
        RecipeUtil.Finish(cycle);
    }
}
