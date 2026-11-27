namespace Sambient.Dsp.Noise;

/// <summary>Small deterministic xorshift64* generator (no allocations, reproducible across platforms).</summary>
internal sealed class NoiseRng
{
    private ulong _s;
    public NoiseRng(ulong seed) { _s = seed * 0x9E3779B97F4A7C15UL + 0x1234567UL; if (_s == 0) _s = 1; for (int i = 0; i < 4; i++) Next(); }
    public ulong Next()
    {
        _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27;
        return _s * 0x2545F4914F6CDD1DUL;
    }
    /// <summary>Uniform in [0,1).</summary>
    public double U() => (Next() >> 11) * (1.0 / 9007199254740992.0);
    /// <summary>Uniform white noise in [-1,1).</summary>
    public double White() => U() * 2.0 - 1.0;
    public double Range(double a, double b) => a + (b - a) * U();
    /// <summary>Log-uniform between a and b.</summary>
    public double LogRange(double a, double b) => a * Math.Pow(b / a, U());
    /// <summary>Exponential inter-arrival time for a Poisson process of the given rate (events/s).</summary>
    public double Exp(double rate) => -Math.Log(1.0 - U()) / rate;
}

/// <summary>Zavalishin/Cytomic TPT state-variable filter; coefficients may change every sample.</summary>
internal struct NoiseSvf
{
    private double _g, _k, _a1, _a2, _a3, _ic1, _ic2;
    public double Lp, Bp, Hp; // Bp is normalised to unity gain at the centre

    public void Set(double fc, double q, double sampleRate)
    {
        if (fc > 0.45 * sampleRate) fc = 0.45 * sampleRate;
        if (fc < 5) fc = 5;
        _g = Math.Tan(Math.PI * fc / sampleRate);
        _k = 1.0 / q;
        _a1 = 1.0 / (1.0 + _g * (_g + _k));
        _a2 = _g * _a1;
        _a3 = _g * _a2;
    }

    public void Process(double x)
    {
        double v3 = x - _ic2;
        double v1 = _a1 * _ic1 + _a2 * v3;
        double v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
        _ic1 = 2 * v1 - _ic1;
        _ic2 = 2 * v2 - _ic2;
        Lp = v2;
        Bp = _k * v1;
        Hp = x - _k * v1 - v2;
    }
}

/// <summary>
/// Smooth modulation source that is exactly periodic over 8 s (sum of sines at integer cycles per loop),
/// so the loop seam of slowly varying envelopes is seamless by construction. Output in [-1,1].
/// </summary>
internal sealed class PeriodicMod
{
    private readonly int[] _k;
    private readonly double[] _a, _ph;
    private readonly double _norm;
    private readonly double _period;

    public PeriodicMod(NoiseRng rng, double periodSeconds, params int[] cycles)
    {
        _period = periodSeconds;
        _k = cycles; _a = new double[cycles.Length]; _ph = new double[cycles.Length];
        double s = 0;
        for (int i = 0; i < cycles.Length; i++)
        {
            _a[i] = 1.0 / Math.Sqrt(cycles[i]);
            _ph[i] = rng.U() * 2 * Math.PI;
            s += _a[i];
        }
        _norm = 1.0 / s;
    }

    public double Eval(double t)
    {
        double w = 2 * Math.PI * t / _period, v = 0;
        for (int i = 0; i < _k.Length; i++) v += _a[i] * Math.Sin(w * _k[i] + _ph[i]);
        return v * _norm;
    }
}
