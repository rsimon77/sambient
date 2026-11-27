namespace Sambient.App.Controls;

/// <summary>
/// Display-only LFO waveform functions for the previews (the audible implementation lives in the engine). The shape index is
/// the <c>Sambient.Engine.LfoShape</c> value. The random shapes use a fixed pseudo-random sequence so the picture is stable;
/// RMP+/RMP- are drawn as a one-shot ramp that then holds (assumption); the "low" variants run at 1/8 of the base rate.
/// </summary>
public static class LfoShapes
{
    public static readonly string[] Names =
    [
        "SINE", "SQAR", "TRI", "SAW", "R.SAW", "RND", "S.RND", "LOG", "R.LOG", "PL.10", "PL.25", "PL.75", "PL.90",
        "STP.2", "STP.3", "STP.4", "STP.5", "STP.6", "STP.7", "RMP+", "RMP−", "L.SIN", "L.TRI", "L.S.RN",
    ];

    /// <summary>Number of cycles the preview window shows for a normal-speed shape.</summary>
    public const double WindowCycles = 2.0;

    /// <summary>Value -1..+1 at time <paramref name="t"/> (in cycles of the base rate, 0..<see cref="WindowCycles"/>).</summary>
    public static double Eval(int shape, double t)
    {
        bool low = shape is 21 or 22 or 23;
        if (low) t /= 8.0;
        double p = t - Math.Floor(t);                     // phase 0..1 within the cycle
        switch (shape)
        {
            case 0: case 21: return Math.Sin(2 * Math.PI * p);
            case 1: return p < 0.5 ? 1 : -1;
            case 2: case 22: return p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4;
            case 3: return 2 * p - 1;
            case 4: return 1 - 2 * p;
            case 5: return Hash((int)Math.Floor(t * 4)) * 2 - 1;                  // sample & hold, 4 steps per cycle
            case 6: case 23: return SmoothRandom(t * 4);
            case 7: return 2 * (1 - Math.Exp(-4 * p)) / (1 - Math.Exp(-4)) - 1;
            case 8: return 1 - 2 * (1 - Math.Exp(-4 * (1 - p))) / (1 - Math.Exp(-4));
            case 9: return p < 0.10 ? 1 : -1;
            case 10: return p < 0.25 ? 1 : -1;
            case 11: return p < 0.75 ? 1 : -1;
            case 12: return p < 0.90 ? 1 : -1;
            case >= 13 and <= 18:
            {
                int n = shape - 11;                                               // STP.2 .. STP.7 -> 2..7 steps
                return Math.Floor(p * n) / (n - 1) * 2 - 1;
            }
            case 19: return t >= WindowCycles * 0.9 ? 1 : Math.Clamp(t / (WindowCycles * 0.9), 0, 1) * 2 - 1;
            case 20: return t >= WindowCycles * 0.9 ? -1 : 1 - Math.Clamp(t / (WindowCycles * 0.9), 0, 1) * 2;
            default: return 0;
        }
    }

    private static double Hash(int i)
    {
        uint x = (uint)(i * 2654435761u + 12345u);
        x ^= x >> 15; x *= 2246822519u; x ^= x >> 13;
        return (x & 0xFFFF) / 65535.0;
    }

    private static double SmoothRandom(double x)
    {
        int i = (int)Math.Floor(x);
        double f = x - i, s = f * f * (3 - 2 * f);
        return (Hash(i) + (Hash(i + 1) - Hash(i)) * s) * 2 - 1;
    }
}
