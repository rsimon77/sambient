namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Five phase-locked octave layers (x1, x2, x4, x8, x16). Higher layers fade in one after another while every layer's
/// shape morphs sine -> triangle -> saw -> square.
/// </summary>
public sealed class Octv : IWaveRecipe
{
    public int Index => 9;
    public string ShortName => "OCTV";
    public string Description => "Octave-locked stack of basic waveforms: sine alone to five layers of square/saw/triangle";

    private static double[] Knots = [0.0, 0.145, 0.198, 0.236, 0.271, 0.3, 0.327, 0.358, 0.391, 0.422, 0.453, 0.492, 0.522, 0.561, 0.621, 0.766, 1.0];   // path-equalizing HARMONIC warp (generated; see RecipeUtil.Warp)

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.Warp(RecipeUtil.U(frame), Knots);
        var amp = new double[1025];
        for (int k = 0; k < 5; k++)
        {
            int r = 1 << k;
            double a = k == 0 ? 1.0 : RecipeUtil.Smooth((u - 0.17 * k + 0.05) / 0.30);
            double gain = 1.0 / (1.0 + 0.25 * k);
            double q = 3.0 * RecipeUtil.Clamp01((u - 0.05 * k) / 0.85);   // shape position 0..3
            int s0 = (int)Math.Floor(q); if (s0 > 2) s0 = 2;
            double f = q - s0;
            for (int h = 1; r * h <= 1024; h++)
                amp[r * h] += a * gain * ((1.0 - f) * RecipeUtil.SineCoef(s0, h) + f * RecipeUtil.SineCoef(s0 + 1, h));
        }
        RecipeUtil.AdditiveSines(amp, cycle);
        RecipeUtil.Finish(cycle);
    }
}
