namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Sawtooth + an additive overtone group (a bump of partials whose centre travels 2 -> 12) + an osc-synced sine
/// (restarts every cycle, ratio 1 -> 7). Frame 0 is a plain soft saw.
/// </summary>
public sealed class Hrm1 : IWaveRecipe
{
    public int Index => 17;
    public string ShortName => "HRM.1";
    public string Description => "Sawtooth plus additive overtone bump plus hard-synced sine overtone";

    public void RenderCycle(int frame, Span<float> cycle) => Core(RecipeUtil.U(frame), cycle);

    /// <summary>Shared with <see cref="Hrm2"/>; u = 0..1.</summary>
    internal static void Core(double u, Span<float> cycle)
    {
        var amp = new double[257];
        double wAdd = 1.1 * RecipeUtil.Smooth(u * 2.2);
        double centre = 2.0 + 10.0 * u;
        for (int h = 1; h < amp.Length; h++)
        {
            double saw = 1.0 / h / (1.0 + Math.Pow(h / 90.0, 2));
            double d = (h - centre) / 2.5;
            amp[h] = saw + wAdd * 0.55 * Math.Exp(-d * d) * (h > 1 ? 1.0 : 0.0) / Math.Sqrt(h);
        }
        RecipeUtil.AdditiveSines(amp, cycle);

        double wSync = 0.5 * RecipeUtil.Smooth((u + 0.1) / 0.9), ratio = 1.0 + 6.0 * u;
        if (wSync > 1e-6)
        {
            var sync = new float[cycle.Length];
            RecipeUtil.RenderOversampled(sync, 4, t => Math.Sin(RecipeUtil.TwoPi * ratio * t));
            RecipeUtil.Soften(sync, 120);
            for (int i = 0; i < cycle.Length; i++) cycle[i] += (float)(wSync * sync[i]);
        }
        RecipeUtil.Finish(cycle);
    }
}
