using System.Runtime.CompilerServices;

namespace Sambient.Dsp.Wavetables;

/// <summary>
/// Allocation-free wavetable oscillator (mutable struct; keep it in a field/array element, never copy it by accident).
/// Audio-thread safe: no locks, no allocation. Not thread-safe itself: use from one thread at a time.
/// - HARMONIC 0..127 interpolates linearly between neighbouring frames (continuous, modulatable per sample).
/// - Mip level from the fundamental so harmonics stay below 0.45*fs, with a short crossfade between two levels.
/// - Hermite (Catmull-Rom) interpolation inside the table.
/// The Wavetable passed to SetWave is immutable and may be shared by any number of oscillators.
/// </summary>
public struct WavetableOscillator
{
    private const double LimitFraction = 0.45;   // highest allowed harmonic frequency / fs
    private const double FadeOctaves = 0.15;     // crossfade width in octaves (0.45 -> ~0.5 fs, no aliasing)

    private Wavetable? _wave;
    private double _phase;      // cycles, [0,1)
    private double _inc;        // cycles per sample (may be negative)
    // level selection (cached by SetFrequency)
    private int _levelHi;       // brighter level
    private int _levelLo;       // darker level (weight 1 - _wHi); equals _levelHi when no crossfade
    private float _wHi;
    // frame selection (cached by SetPosition)
    private int _f0, _f1;
    private float _ff;

    public double Phase => _phase;

    /// <summary>Sets the phase in cycles (any value; wrapped into [0,1)).</summary>
    public void Reset(double phase = 0.0)
    {
        double p = phase - Math.Floor(phase);
        _phase = (p >= 1.0 || double.IsNaN(p)) ? 0.0 : p;
    }

    public void SetWave(Wavetable wave) => _wave = wave;

    /// <summary>Sets frequency; any value incl. 0 and negative (reverse playback) is accepted.</summary>
    public void SetFrequency(double hz, double sampleRate)
    {
        double inc = hz / sampleRate;
        if (double.IsNaN(inc)) inc = 0;
        _inc = inc;
        double a = Math.Abs(inc);
        if (a <= 0) { _levelHi = _levelLo = 0; _wHi = 1f; return; }
        // continuous level coordinate: Lc == k  <=>  level k's top harmonic sits exactly at 0.45 fs
        double lc = Math.Log2(Wavetable.TopHarmonics * a / LimitFraction);
        if (lc <= 0) { _levelHi = _levelLo = 0; _wHi = 1f; return; }
        if (lc >= Wavetable.LevelCount - 1) { _levelHi = _levelLo = Wavetable.LevelCount - 1; _wHi = 1f; return; }
        int n = (int)lc;
        double x = lc - n;
        _levelHi = n;
        _levelLo = n + 1;
        _wHi = (float)Math.Max(0.0, 1.0 - x / FadeOctaves);
    }

    /// <summary>HARMONIC 0..127 (continuous). Out-of-range values are clamped.</summary>
    public void SetPosition(float harmonic)
    {
        if (!(harmonic > 0f)) harmonic = 0f;           // also catches NaN
        else if (harmonic > Wavetable.FrameCount - 1) harmonic = Wavetable.FrameCount - 1;
        int f0 = (int)harmonic;
        if (f0 >= Wavetable.FrameCount - 1) { _f0 = _f1 = Wavetable.FrameCount - 1; _ff = 0f; return; }
        _f0 = f0; _f1 = f0 + 1; _ff = harmonic - f0;
    }

    /// <summary>Returns one sample and advances the phase. Outputs 0 if no wave is set.</summary>
    public float Next()
    {
        var wave = _wave;
        if (wave == null) return 0f;
        float y = Read(wave, _levelHi);
        if (_wHi < 1f)
        {
            float lo = Read(wave, _levelLo);
            y = lo + (y - lo) * _wHi;
        }
        double p = _phase + _inc;
        p -= Math.Floor(p);
        _phase = p >= 1.0 ? 0.0 : p;
        return y;
    }

    /// <summary>
    /// Fills <paramref name="output"/>. If <paramref name="positionPerSample"/> is non-empty it supplies HARMONIC per sample
    /// (audio-rate modulation, length must be &gt;= output.Length); otherwise the value from SetPosition is used.
    /// </summary>
    public void Process(Span<float> output, ReadOnlySpan<float> positionPerSample)
    {
        if (positionPerSample.IsEmpty)
        {
            for (int i = 0; i < output.Length; i++) output[i] = Next();
        }
        else
        {
            int n = Math.Min(output.Length, positionPerSample.Length);
            for (int i = 0; i < n; i++)
            {
                SetPosition(positionPerSample[i]);
                output[i] = Next();
            }
            output.Slice(n).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly float Read(Wavetable wave, int level)
    {
        float[] d = wave.LevelData(level);
        int len = Wavetable.TableLength(level);
        int stride = len + 3;
        double x = _phase * len;
        int i = (int)x;
        if (i >= len) i = len - 1;
        float t = (float)(x - i);
        int b0 = _f0 * stride + i;
        int b1 = _f1 * stride + i;
        float ff = _ff;
        float y0 = d[b0] + (d[b1] - d[b0]) * ff;
        float y1 = d[b0 + 1] + (d[b1 + 1] - d[b0 + 1]) * ff;
        float y2 = d[b0 + 2] + (d[b1 + 2] - d[b0 + 2]) * ff;
        float y3 = d[b0 + 3] + (d[b1 + 3] - d[b0 + 3]) * ff;
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * t + c2) * t + c1) * t + y1;
    }
}
