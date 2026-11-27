namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Vowel formants a-e-i-o-u across the frames (F1/F2/F3 of a male voice, interpolated smoothly) on a glottal-like
/// 1/h^1.1 source, assuming a fundamental of 110 Hz to place the formants on harmonic numbers; a small sinusoidal
/// phase modulation (index 0 -> 0.7 -> 0) adds a vocal-flutter FM colouring in the middle of the sweep.
/// </summary>
public sealed class Frm2 : IWaveRecipe
{
    public int Index => 22;
    public string ShortName => "FRM.2";
    public string Description => "Vowel formants a-e-i-o-u across HARMONIC, with a touch of FM flutter";

    private const double F0 = 110.0;
    // F1, F2, F3 in Hz for a, e, i, o, u
    private static readonly double[][] Vowels =
    [
        [800, 1150, 2900],
        [400, 2000, 2800],
        [280, 2250, 2900],
        [450, 830, 2830],
        [325, 700, 2530],
    ];
    private static readonly double[] Gain = [1.0, 0.75, 0.4];
    private static readonly double[] BwHz = [110, 130, 190];

    public void RenderCycle(int frame, Span<float> cycle)
    {
        double u = RecipeUtil.U(frame);
        double v = 4.0 * u;
        int i0 = Math.Min(3, (int)Math.Floor(v));
        double f = RecipeUtil.Smooth(v - i0);
        var fc = new double[3];
        for (int k = 0; k < 3; k++) fc[k] = Math.Exp((1 - f) * Math.Log(Vowels[i0][k]) + f * Math.Log(Vowels[i0 + 1][k])) / F0;

        var amp = new double[600];
        for (int h = 1; h < amp.Length; h++)
        {
            double env = 0.06;
            for (int k = 0; k < 3; k++)
            {
                double d = (h - fc[k]) / (BwHz[k] * 1.25 / F0);
                env += Gain[k] / (1.0 + d * d);
            }
            amp[h] = Math.Pow(h, -1.1) * env;
        }
        var voice = new float[cycle.Length];
        RecipeUtil.AdditiveSines(amp, voice);

        double idx = 0.7 * Math.Sin(Math.PI * u);   // FM colouring: time-warp the cycle by a sine of itself
        for (int i = 0; i < cycle.Length; i++)
        {
            double t = (double)i / cycle.Length;
            cycle[i] = (float)RecipeUtil.ReadCubic(voice, t + idx / RecipeUtil.TwoPi * Math.Sin(RecipeUtil.TwoPi * t));
        }
        RecipeUtil.Finish(cycle);
    }
}
