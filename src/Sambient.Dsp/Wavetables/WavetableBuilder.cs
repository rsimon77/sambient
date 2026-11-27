using System.Diagnostics;

namespace Sambient.Dsp.Wavetables;

/// <summary>Builds band-limited mip-mapped <see cref="Wavetable"/>s from recipes. Not real-time safe; call from a background thread.</summary>
public static class WavetableBuilder
{
    public static Wavetable Build(IWaveRecipe recipe)
    {
        var sw = Stopwatch.StartNew();
        const int R = Wavetable.CycleResolution;
        const int Frames = Wavetable.FrameCount;
        const int Levels = Wavetable.LevelCount;

        var fftR = Fft.GetShared(R);
        var levels = new float[Levels][];
        var ffts = new Fft[Levels];
        for (int k = 0; k < Levels; k++)
        {
            levels[k] = new float[Frames * Wavetable.Stride(k)];
            ffts[k] = Fft.GetShared(Wavetable.TableLength(k));
        }

        var cycle = new float[R];
        var re = new double[R];
        var im = new double[R];
        var lre = new double[Wavetable.TableLength(0)];
        var lim = new double[Wavetable.TableLength(0)];
        double peak = 0; // peak of level 0 (the full-bandwidth waveform) over all frames

        for (int frame = 0; frame < Frames; frame++)
        {
            recipe.RenderCycle(frame, cycle);
            for (int i = 0; i < R; i++) { re[i] = cycle[i]; im[i] = 0; }
            fftR.Forward(re, im);

            for (int k = 0; k < Levels; k++)
            {
                int h = Wavetable.MaxHarmonics(k);
                int n = Wavetable.TableLength(k);
                Array.Clear(lre, 0, n); Array.Clear(lim, 0, n);
                double scale = (double)n / R;
                double taperStart = 0.8 * h;
                for (int b = 1; b <= h; b++) // bin 0 (DC) is dropped
                {
                    double w = 1.0;
                    if (b > taperStart) // light raised-cosine taper on the top 20 % against Gibbs ringing
                        w = 0.5 * (1.0 + Math.Cos(Math.PI * (b - taperStart) / (h - taperStart + 1.0)));
                    double s = scale * w;
                    lre[b] = re[b] * s; lim[b] = im[b] * s;
                    lre[n - b] = re[b] * s; lim[n - b] = -im[b] * s;
                }
                ffts[k].Inverse(lre.AsSpan(0, n), lim.AsSpan(0, n));

                var dst = levels[k];
                int baseIdx = frame * (n + 3);
                for (int i = 0; i < n; i++)
                {
                    float v = (float)lre[i];
                    dst[baseIdx + 1 + i] = v;
                    if (k == 0) { double a = Math.Abs(v); if (a > peak) peak = a; }
                }
                dst[baseIdx] = dst[baseIdx + n];
                dst[baseIdx + n + 1] = dst[baseIdx + 1];
                dst[baseIdx + n + 2] = dst[baseIdx + 2];
            }
        }

        // Per-WAVE normalization: level 0 of the loudest frame peaks at 1.0, relative frame loudness is kept.
        // Darker levels may exceed 1.0 slightly (e.g. the fundamental of a square is 4/pi ~ 1.27); that is physical, not clipped.
        if (peak > 1e-12)
        {
            float g = (float)(1.0 / peak);
            foreach (var l in levels)
                for (int i = 0; i < l.Length; i++) l[i] *= g;
        }
        sw.Stop();
        return new Wavetable(recipe.Index, recipe.ShortName, recipe.Description, levels, sw.Elapsed);
    }
}
