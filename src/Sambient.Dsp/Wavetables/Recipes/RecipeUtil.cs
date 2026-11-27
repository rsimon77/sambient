namespace Sambient.Dsp.Wavetables.Recipes;

internal static class RecipeUtil
{
    /// <summary>frame 0..127 -> 0..1</summary>
    public static double U(int frame) => frame / 127.0;

    /// <summary>cycle[n] = sum_h amp[h] * sin(2 pi h n / N) for h = 1..amp.Length-1 (amp[0] ignored).</summary>
    public static void AdditiveSines(ReadOnlySpan<double> amp, Span<float> cycle)
    {
        int n = cycle.Length;
        var fft = Fft.GetShared(n);
        var re = new double[n];
        var im = new double[n];
        int hMax = Math.Min(amp.Length - 1, n / 2 - 1);
        for (int h = 1; h <= hMax; h++)
        {
            // a*sin(h t) = Im(a e^{iht}): inverse-FFT bins  X[h] = -i a N/2, X[N-h] = +i a N/2
            im[h] = -amp[h] * n * 0.5;
            im[n - h] = amp[h] * n * 0.5;
        }
        fft.Inverse(re, im);
        for (int i = 0; i < n; i++) cycle[i] = (float)re[i];
    }
}
