namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>PWM square, duty 50 % -> ~3 %. Edges are box-filtered (exact pixel coverage), so the duty moves continuously with sub-sample precision.</summary>
public sealed class SqrD : IWaveRecipe
{
    public int Index => 2;
    public string ShortName => "SQR.D";
    public string Description => "PWM square, duty cycle 50% to 3%";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        int n = cycle.Length;
        double duty = 0.5 - 0.47 * RecipeUtil.U(frame);
        double dn = duty * n;
        for (int i = 0; i < n; i++)
        {
            double cover = Math.Clamp(dn - i, 0.0, 1.0); // fraction of [i, i+1) that is high
            cycle[i] = (float)(2.0 * cover - 1.0);
        }
    }
}
