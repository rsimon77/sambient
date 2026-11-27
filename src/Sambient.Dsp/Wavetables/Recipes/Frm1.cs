namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Formant sweep over an FM sine: the source is sin(t + I sin t) (I = 0.4 -> 3.9) plus a thin 1/h saw bed, shaped by a
/// resonant peak in the harmonic domain whose centre wanders 2 -> 18 (with a slow wobble), plus a low floor so the fundamental survives.
/// </summary>
public sealed class Frm1 : IWaveRecipe
{
    public int Index => 21;
    public string ShortName => "FRM.1";
    public string Description => "FM sine with a moving formant peak (centre 2 to 18 harmonics)";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double idx = 0.4 + 3.5 * u;
        double fc = 2.0 + 16.0 * Math.Pow(u, 1.2) + 1.5 * Math.Sin(2.0 * Math.PI * 2.0 * u);
        double bw = 1.3 + 0.1 * fc;

        var saw = new float[cycle.Length];
        var amp = new double[400];
        for (int h = 1; h < amp.Length; h++) amp[h] = 0.35 / h;
        RecipeUtil.AdditiveSines(amp, saw);
        RecipeUtil.Render(cycle, t =>
        {
            double ph = RecipeUtil.TwoPi * t;
            return Math.Sin(ph + idx * Math.Sin(ph));
        });
        for (int i = 0; i < cycle.Length; i++) cycle[i] += saw[i];
        RecipeUtil.ShapeSpectrum(cycle, h =>
        {
            double d = (h - fc) / bw;
            double g = 0.18 + 2.4 * Math.Exp(-0.5 * d * d);
            return h > 300 ? g * 300.0 / h : g;
        });
        RecipeUtil.Finish(cycle);
    }
}
