namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Wave-shaped sine: tanh drive 0.3 -> 12 (log), with a small bias that adds even harmonics in the middle range.</summary>
public sealed class SinS : IWaveRecipe
{
    public int Index => 3;
    public string ShortName => "SIN.S";
    public string Description => "Wave-shaped sine: pure sine to increasingly driven, harmonically rich";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double drive = 0.3 * Math.Pow(40.0, u);
        double bias = 0.8 * u * (1.0 - u);
        int n = cycle.Length;
        double peak = 1e-9;
        for (int i = 0; i < n; i++)
        {
            double s = Math.Sin(2.0 * Math.PI * i / n);
            double y = Math.Tanh(drive * (s + bias));
            cycle[i] = (float)y;
            peak = Math.Max(peak, Math.Abs(y));
        }
        // keep every frame at a similar level (frame 0 would otherwise be a quiet tanh(0.3 s))
        float g = (float)(1.0 / peak);
        for (int i = 0; i < n; i++) cycle[i] *= g;
    }
}
