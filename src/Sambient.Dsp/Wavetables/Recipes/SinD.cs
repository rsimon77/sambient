namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Sine with a modulated duty cycle (phase distortion): the positive half-sine takes a duty fraction of the cycle that shrinks from 50 % to 10 %.</summary>
public sealed class SinD : IWaveRecipe
{
    public int Index => 4;
    public string ShortName => "SIN.D";
    public string Description => "Sine with modulated duty cycle: pure sine to a pinched, pulse-like sine";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        // slow wobble on top of the sweep (zero at both ends of the first frame) so the duty "modulates" instead of just sliding
        double d = 0.5 - 0.40 * u + 0.035 * Math.Sin(RecipeUtil.TwoPi * 3.0 * u) * Math.Sin(Math.PI * u);
        RecipeUtil.Render(cycle, t =>
        {
            double p = t < d ? 0.5 * t / d : 0.5 + 0.5 * (t - d) / (1.0 - d);
            return Math.Sin(RecipeUtil.TwoPi * p);
        });
        RecipeUtil.Finish(cycle);
    }
}
