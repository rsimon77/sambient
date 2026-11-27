namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Two-operator phase modulation at ratio 1:1: y = sin(t + I sin t), index I = 0 -> 8.</summary>
public sealed class FmPf : IWaveRecipe
{
    public int Index => 25;
    public string ShortName => "FM.PF";
    public string Description => "2-operator FM (phase modulation), ratio 1:1, index 0 to 8";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double index = 8.0 * RecipeUtil.U(frame);
        int n = cycle.Length;
        for (int i = 0; i < n; i++)
        {
            double t = 2.0 * Math.PI * i / n;
            cycle[i] = (float)Math.Sin(t + index * Math.Sin(t));
        }
    }
}
