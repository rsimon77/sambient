namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>HRM.1 (evolving at 40 % of its own sweep) plus an FM overtone voice sin(3t + I sin 2t), I = 0 -> 5 (odd-harmonic sidebands).</summary>
public sealed class Hrm2 : IWaveRecipe
{
    public int Index => 18;
    public string ShortName => "HRM.2";
    public string Description => "HRM.1 wave plus an FM-modulated overtone voice";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        Hrm1.Core(0.4 * u, cycle);
        double w = 0.8 * RecipeUtil.Smooth(u * 1.5), idx = 5.0 * u;
        if (w > 1e-6)
        {
            var fm = new float[cycle.Length];
            RecipeUtil.Render(fm, t =>
            {
                double ph = RecipeUtil.TwoPi * t;
                return Math.Sin(3.0 * ph + idx * Math.Sin(2.0 * ph));
            });
            for (int i = 0; i < cycle.Length; i++) cycle[i] += (float)(w * fm[i]);
        }
        RecipeUtil.Finish(cycle);
    }
}
