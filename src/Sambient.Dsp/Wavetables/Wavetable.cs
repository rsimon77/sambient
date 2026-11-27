namespace Sambient.Dsp.Wavetables;

/// <summary>
/// One built WAVE: 128 frames x 11 band-limited mip levels. Immutable after construction, so it can be
/// shared freely between threads (including the audio thread).
/// Level k contains harmonics 1..(1024 >> k) (top 20 % tapered) and has TableLength(k) samples per cycle.
/// Internal storage per level: frame-major blocks of (length + 3) floats with guard points
/// [t[N-1], t[0..N-1], t[0], t[1]] so cubic interpolation needs no wrapping.
/// </summary>
public sealed class Wavetable
{
    public const int FrameCount = 128;
    public const int LevelCount = 11;
    public const int CycleResolution = 8192;
    public const int TopHarmonics = 1024;

    /// <summary>Highest harmonic kept in <paramref name="level"/> (1024, 512, ... 1).</summary>
    public static int MaxHarmonics(int level) => TopHarmonics >> level;
    /// <summary>
    /// Samples per cycle in <paramref name="level"/>: 4*maxHarm for levels 0-1 (only used below ~40 Hz, where the top
    /// harmonics are inaudible), otherwise max(8*maxHarm, 256). More oversampling keeps the cubic-interpolation images
    /// of the top harmonics low (error falls with (maxHarm/length)^2). Total 11264 floats per frame = 5.8 MB per WAVE.
    /// </summary>
    public static int TableLength(int level) => level < 2 ? 4 * MaxHarmonics(level) : Math.Max(8 * MaxHarmonics(level), 256);
    public static int Stride(int level) => TableLength(level) + 3;

    private readonly float[][] _levels;

    public int Index { get; }
    public string ShortName { get; }
    public string Description { get; }
    /// <summary>Wall-clock time the build took.</summary>
    public TimeSpan BuildTime { get; }
    /// <summary>Float storage in bytes.</summary>
    public long MemoryBytes { get; }

    internal Wavetable(int index, string shortName, string description, float[][] levels, TimeSpan buildTime)
    {
        Index = index; ShortName = shortName; Description = description; _levels = levels; BuildTime = buildTime;
        long n = 0;
        foreach (var l in levels) n += (long)l.Length * sizeof(float);
        MemoryBytes = n;
    }

    internal float[] LevelData(int level) => _levels[level];

    /// <summary>The N samples (without guard points) of one cycle of a frame at a mip level.</summary>
    public ReadOnlySpan<float> GetCycle(int frame, int level)
    {
        int stride = Stride(level);
        return new ReadOnlySpan<float>(_levels[level], frame * stride + 1, TableLength(level));
    }
}
