namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Sine fundamental plus overtones 2..24 that each swell and fade at their own rate over the HARMONIC range (sin^2 envelopes), so the overtone mix keeps morphing.</summary>
public sealed class SinM : IWaveRecipe
{
    public int Index => 10;
    public string ShortName => "SIN.M";
    public string Description => "Morphing overtones of a sine: each partial swells and fades at its own rate";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        var amp = new double[26];
        amp[1] = 1.0;
        double grow = 0.45 + 0.55 * u;
        for (int h = 2; h <= 24; h++)
        {
            double nu = 0.35 + 0.28 * (h - 2);
            double s = Math.Sin(Math.PI * nu * u);
            amp[h] = 1.5 * Math.Pow(h, -0.85) * s * s * grow;
        }
        RecipeUtil.AdditiveSines(amp, cycle);
        RecipeUtil.Finish(cycle);
    }
}
