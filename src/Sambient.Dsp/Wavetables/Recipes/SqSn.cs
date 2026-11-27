namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Soft square whose phase is shaped by a sine: y = tanh(7 sin(t + I sin t)), I = 0 -> 2.8 (pulse edges swing sinusoidally, extra pulses appear).</summary>
public sealed class SqSn : IWaveRecipe
{
    public int Index => 7;
    public string ShortName => "SQ.SN";
    public string Description => "Square wave shaped by a sine: edges swing and split into extra pulses";

    private static double[] Knots = [0.0, 0.271, 0.343, 0.384, 0.415, 0.444, 0.474, 0.506, 0.542, 0.582, 0.626, 0.674, 0.727, 0.785, 0.849, 0.921, 1.0];   // path-equalizing HARMONIC warp (generated; see RecipeUtil.Warp)

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double idx = 2.8 * RecipeUtil.Warp(RecipeUtil.U(frame), Knots);
        RecipeUtil.Render(cycle, t =>
        {
            double ph = RecipeUtil.TwoPi * t;
            return Math.Tanh(7.0 * Math.Sin(ph + idx * Math.Sin(ph)));
        });
        RecipeUtil.Finish(cycle);
    }
}
