namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Hard sync: a master saw plus three slave saws that restart every cycle. The slave ratios rise with HARMONIC
/// (1 -> 3.5 / 5.5 / 8.0, non-integer is fine because each slave resets with the master). Frame 0 is a plain saw.
/// </summary>
public sealed class Sync : IWaveRecipe
{
    public int Index => 16;
    public string ShortName => "SYNC";
    public string Description => "Hard-synced sawtooth stack: one saw to three sweeping slave saws";

    private static readonly double[] Spread = [0.0, 2.5, 4.5, 7.0];
    private static readonly double[] Weight = [1.0, 0.6, 0.4, 0.3];

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        var ratio = new double[4];
        for (int k = 0; k < 4; k++) ratio[k] = 1.0 + Spread[k] * u;
        RecipeUtil.RenderOversampled(cycle, 4, t =>
        {
            double s = 0;
            for (int k = 0; k < 4; k++) s += Weight[k] * RecipeUtil.Saw(ratio[k] * t);
            return s;
        });
        RecipeUtil.Soften(cycle, 220);
        RecipeUtil.Finish(cycle);
    }
}
