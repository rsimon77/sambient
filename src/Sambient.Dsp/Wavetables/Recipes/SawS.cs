namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>Wave-shaped sawtooth: HARMONIC raises both the saw's brightness (rolloff cutoff 3 -> 400 harmonics) and a tanh shaper drive.</summary>
public sealed class SawS : IWaveRecipe
{
    public int Index => 1;
    public string ShortName => "SAW.S";
    public string Description => "Wave-shaped sawtooth: soft rounded saw to bright, buzzy shaped saw";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double cutoff = 3.0 * Math.Pow(400.0 / 3.0, u);
        var amp = new double[1025];
        for (int h = 1; h < amp.Length; h++)
        {
            double r = h / cutoff;
            amp[h] = (1.0 / h) / (1.0 + r * r * r * r);   // 1/h saw with a 4th-order rolloff
        }
        RecipeUtil.AdditiveSines(amp, cycle);
        double peak = 0;
        foreach (var v in cycle) peak = Math.Max(peak, Math.Abs(v));
        double drive = 0.7 + 3.3 * u;
        double norm = 1.0 / (Math.Max(peak, 1e-9)) * drive, t = 1.0 / Math.Tanh(drive);
        for (int i = 0; i < cycle.Length; i++)
            cycle[i] = (float)(Math.Tanh(cycle[i] * norm) * t);
    }
}
