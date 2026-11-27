namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// White-noise spectrum: every frame is an independent random-phase, Rayleigh-magnitude spectrum with its own fixed seed
/// (so fast HARMONIC modulation, e.g. LFO depth 63 on HARMONIC 63, yields noise). The occupied bandwidth grows
/// exponentially with the frame, 1 -> 1024 harmonics: frame 0 is a pure sine, the top frames are full-band white noise.
/// There is deliberately no continuity between neighbouring frames.
/// </summary>
public sealed class WhNs : IWaveRecipe
{
    public int Index => 32;
    public string ShortName => "WH.NS";
    public string Description => "White-noise spectrum frames (bandwidth 1 to 1024 harmonics); modulate HARMONIC fast for noise";

    public void RenderCycle(int frame, Span<float> cycle)
    {
        const int H = 1024;
        double bw = Math.Pow(H, RecipeUtil.U(frame));
        var rng = new Random(3200 + frame * 104729);
        var amp = new double[H + 1]; var ph = new double[H + 1];
        for (int h = 1; h <= H; h++)
        {
            double edge = RecipeUtil.Clamp01(bw - h + 1.0);
            double rayleigh = Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * 0.7071;
            amp[h] = edge * rayleigh;
            ph[h] = rng.NextDouble() * RecipeUtil.TwoPi;
        }
        if (frame == 0) { amp[1] = 1.0; ph[1] = 0; } else amp[1] = Math.Max(amp[1], 0.7);
        RecipeUtil.AdditivePhased(amp, ph, cycle);
        RecipeUtil.SoftLimit(cycle, 2.0, RecipeUtil.Smooth((RecipeUtil.U(frame) - 0.1) / 0.3));
        RecipeUtil.Finish(cycle);
    }
}
