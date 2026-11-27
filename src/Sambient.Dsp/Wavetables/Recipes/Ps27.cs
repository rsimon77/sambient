namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Sine fundamental plus ONE prominent overtone that visits 27 harmonic positions (2..28) across the HARMONIC range.
/// The position glides with short plateaus on each harmonic; between two harmonics the overtone cross-fades (linear)
/// so the transition is smooth rather than stepped.
/// </summary>
public sealed class Ps27 : IWaveRecipe
{
    public int Index => 23;
    public string ShortName => "27.PS";
    public string Description => "Sine plus one prominent overtone stepping/gliding through 27 harmonic positions";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double x = 26.0 * RecipeUtil.U(frame);
        double fl = Math.Floor(x);
        double stair = fl + RecipeUtil.Smooth((x - fl - 0.1) / 0.8);   // short plateaus, continuous at every integer
        double p = 2.0 + 0.1 * stair + 0.9 * x;
        var amp = new double[48];
        amp[1] = 1.0;
        for (int h = 2; h < amp.Length; h++)
        {
            double d = Math.Abs(h - p);
            if (d < 1.0) amp[h] += 0.38 * (1.0 - d);   // linear cross-fade between neighbouring positions (partition of unity)
            amp[h] += 0.03 / h;   // faint bed so the overtone never sits on a dead spectrum
        }
        RecipeUtil.AdditiveSines(amp, cycle);
        RecipeUtil.Finish(cycle);
    }
}
