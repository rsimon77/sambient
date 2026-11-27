namespace Sambient.Dsp.Wavetables.Recipes;

/// <summary>
/// Morphing random waveforms: 13 keyframe spectra (keyframe 0 = sine, the others random sparse partials with random phases,
/// fundamental with fixed phase and level) placed evenly over the HARMONIC range and joined by Catmull-Rom interpolation of the
/// sine/cosine coefficients, so the sound drifts from one random shape to the next without jumps.
/// </summary>
public sealed class SRnd : IWaveRecipe
{
    public int Index => 30;
    public string ShortName => "S.RND";
    public string Description => "Smoothly morphing random waveforms (13 keyframes)";

    private const int Keys = 13, MaxH = 40;

    private static readonly Lazy<double[][]> Coef = new(() =>
    {
        // per keyframe: [0..MaxH] sine coefs, [MaxH+1 .. 2*MaxH+1] cosine coefs
        var rng = new Random(3001);
        var k = new double[Keys][];
        for (int j = 0; j < Keys; j++)
        {
            var c = new double[2 * (MaxH + 1)];
            if (j == 0) c[1] = 1.0;
            else
            {
                int count = 4 + rng.Next(9);
                double tilt = 0.2 + 0.7 * rng.NextDouble();
                for (int m = 0; m < count; m++)
                {
                    int h = 2 + rng.Next(MaxH - 1);
                    double a = Math.Pow(h, -tilt) * (0.25 + 0.5 * rng.NextDouble()), p = rng.NextDouble() * RecipeUtil.TwoPi;
                    c[h] += a * Math.Cos(p); c[MaxH + 1 + h] += a * Math.Sin(p);
                }
                c[1] = 1.0;   // the fundamental keeps a fixed phase and level in every keyframe: no cancellation while interpolating
            }
            k[j] = c;
        }
        return k;
    });

    public void RenderCycle(int frame, Span<float> cycle)
    {
        var keys = Coef.Value;
        double x = RecipeUtil.U(frame) * (Keys - 1);
        int i1 = Math.Min(Keys - 2, (int)Math.Floor(x));
        double f = x - i1;
        var k0 = keys[Math.Max(0, i1 - 1)]; var k1 = keys[i1]; var k2 = keys[i1 + 1]; var k3 = keys[Math.Min(Keys - 1, i1 + 2)];
        var amp = new double[MaxH + 1]; var ph = new double[MaxH + 1];
        for (int h = 1; h <= MaxH; h++)
        {
            double sc = Cr(k0[h], k1[h], k2[h], k3[h], f), cc = Cr(k0[MaxH + 1 + h], k1[MaxH + 1 + h], k2[MaxH + 1 + h], k3[MaxH + 1 + h], f);
            amp[h] = Math.Sqrt(sc * sc + cc * cc); ph[h] = Math.Atan2(cc, sc);
        }
        RecipeUtil.AdditivePhased(amp, ph, cycle);
        RecipeUtil.Finish(cycle);
    }

    private static double Cr(double y0, double y1, double y2, double y3, double f) =>
        y1 + 0.5 * f * (y2 - y0 + f * (2.0 * y0 - 5.0 * y1 + 4.0 * y2 - y3 + f * (3.0 * (y1 - y2) + y3 - y0)));
}
