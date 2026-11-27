using System.Diagnostics.CodeAnalysis;

namespace Sambient.Dsp.Wavetables;

/// <summary>
/// Radix-2 in-place complex FFT / IFFT (double precision) with precomputed twiddles and bit-reversal table.
/// Allocation-free after construction. Instances are immutable after construction and thread-safe
/// (they hold no scratch state); the caller supplies the re/im buffers.
/// Sizes: powers of two, 2..16384.
/// </summary>
public sealed class Fft
{
    public const int MaxSize = 16384;

    private readonly int _n;
    private readonly double[] _cos; // cos(2*pi*k/N), k < N/2
    private readonly double[] _sin; // sin(2*pi*k/N)
    private readonly int[] _rev;

    public int Size => _n;

    public Fft(int size)
    {
        if (size < 2 || size > MaxSize || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "FFT size must be a power of two in 2..16384.");
        _n = size;
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double a = 2.0 * Math.PI * k / size;
            _cos[k] = Math.Cos(a);
            _sin[k] = Math.Sin(a);
        }
        _rev = new int[size];
        int bits = System.Numerics.BitOperations.Log2((uint)size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
    }

    private static readonly Dictionary<int, Fft> Shared = new();

    /// <summary>Returns a cached instance for the size (creating it under a lock on first use). Not for the audio thread.</summary>
    public static Fft GetShared(int size)
    {
        lock (Shared)
        {
            if (!Shared.TryGetValue(size, out var f))
                Shared[size] = f = new Fft(size);
            return f;
        }
    }

    /// <summary>Forward transform, X[k] = sum x[n] e^{-2 pi i k n / N}. Unnormalized.</summary>
    public void Forward(Span<double> re, Span<double> im) => Transform(re, im, inverse: false);

    /// <summary>Inverse transform, scaled by 1/N so Inverse(Forward(x)) == x.</summary>
    public void Inverse(Span<double> re, Span<double> im)
    {
        Transform(re, im, inverse: true);
        double s = 1.0 / _n;
        for (int i = 0; i < _n; i++) { re[i] *= s; im[i] *= s; }
    }

    private void Transform(Span<double> re, Span<double> im, bool inverse)
    {
        int n = _n;
        if (re.Length < n || im.Length < n) throw new ArgumentException("Buffers shorter than FFT size.");
        var rev = _rev;
        for (int i = 0; i < n; i++)
        {
            int j = rev[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        double sign = inverse ? 1.0 : -1.0;
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len;
            for (int start = 0; start < n; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = _cos[k * step];
                    double wi = sign * _sin[k * step];
                    int a = start + k, b = a + half;
                    double tr = re[b] * wr - im[b] * wi;
                    double ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
            }
        }
    }
}
