namespace Sambient.Dsp.Noise;

/// <summary>An immutable stereo sample for the NOISE layer (default: 8 s, 32 kHz, seamless loop).</summary>
public sealed class NoiseSample
{
    public NoiseSample(string name, int sampleRate, float[] left, float[] right)
    {
        Name = name; SampleRate = sampleRate; Left = left; Right = right;
    }
    public string Name { get; }
    public int SampleRate { get; }
    public float[] Left { get; }
    public float[] Right { get; }
    public int Length => Left.Length;
}

/// <summary>
/// 8 sample slots (1..8). Slots are built lazily on a background thread from <see cref="NoiseTextures"/>;
/// <see cref="Get"/> is lock-free and allocation-free. Built samples are published with Volatile.Write.
/// </summary>
public sealed class NoiseBank
{
    public const int SlotCount = 8;
    public static readonly string[] DefaultNames = { "RAIN", "FIRE", "RMBL", "FRST", "SEA", "WIND", "WODS", "FLUT" };

    private readonly NoiseSample?[] _samples = new NoiseSample?[SlotCount + 1];
    private readonly Task?[] _tasks = new Task?[SlotCount + 1];
    private readonly object _lock = new();

    private static bool Valid(int slot) => slot >= 1 && slot <= SlotCount;

    /// <summary>Starts building the slot if needed (non-RT). Returns the same task on repeated calls.</summary>
    public Task EnsureBuiltAsync(int slot)
    {
        if (!Valid(slot)) return Task.CompletedTask;
        lock (_lock)
        {
            return _tasks[slot] ??= Task.Run(() =>
            {
                var s = NoiseTextures.Generate(slot);
                // A user sample set while building wins.
                Interlocked.CompareExchange(ref _samples[slot], s, null);
            });
        }
    }

    public bool IsReady(int slot) => Valid(slot) && Volatile.Read(ref _samples[slot]) != null;

    /// <summary>Audio-thread safe. Null while not built.</summary>
    public NoiseSample? Get(int slot) => Valid(slot) ? Volatile.Read(ref _samples[slot]) : null;

    /// <summary>Replaces a slot (user sample, non-RT); publication is atomic.</summary>
    public void Set(int slot, NoiseSample sample)
    {
        if (!Valid(slot)) return;
        lock (_lock) { _tasks[slot] = Task.CompletedTask; }
        Volatile.Write(ref _samples[slot], sample);
    }
}

public enum SamplePlayMode { Loop, OneShot }

/// <summary>
/// Stereo sample voice for the NOISE layer. Cubic (Hermite) interpolation, allocation-free.
/// Mutable struct: keep as a field/array element.
/// </summary>
public struct SamplePlayer
{
    private const int FadeInSamples = 96;     // ~2 ms at 48 kHz (output samples)
    private const double FadeOutSource = 64;  // one-shot fade-out length in source samples

    private float[]? _l, _r;
    private int _len;
    private double _pos;
    private double _rate;
    private SamplePlayMode _mode;
    private bool _playing;
    private int _fade;

    public void SetSample(NoiseSample? sample)
    {
        if (sample == null || sample.Length < 2) { _l = null; _r = null; _len = 0; _playing = false; return; }
        _l = sample.Left; _r = sample.Right; _len = Math.Min(sample.Left.Length, sample.Right.Length);
        _playing = false;
    }

    public void SetMode(SamplePlayMode mode) => _mode = mode;

    /// <summary>Playback ratio relative to the sample's ORIGINAL pitch, already including sample-rate conversion
    /// (sample.SampleRate / outputRate) × 2^(semitones/12). Can change every sample (pitch modulation).</summary>
    public void SetRate(double ratio) => _rate = ratio;

    /// <summary>(Re)starts from position 0 with a ~2 ms fade-in (both modes; deterministic).</summary>
    public void Start()
    {
        _pos = 0; _fade = 0;
        _playing = _l != null;
    }

    public readonly bool IsPlaying => _playing;

    public void Next(out float left, out float right)
    {
        if (!_playing || _l == null || _r == null) { left = 0; right = 0; return; }

        double pos = _pos;
        int i = (int)pos;
        float x = (float)(pos - i);
        float l, r;
        if (_mode == SamplePlayMode.Loop)
        {
            int n = _len;
            int i0 = i, im1 = i - 1, i1 = i + 1, i2 = i + 2;
            if (im1 < 0) im1 += n;
            if (i1 >= n) i1 -= n;
            if (i2 >= n) i2 -= n;
            l = Hermite(_l[im1], _l[i0], _l[i1], _l[i2], x);
            r = Hermite(_r[im1], _r[i0], _r[i1], _r[i2], x);
        }
        else
        {
            int last = _len - 1;
            int im1 = i - 1 < 0 ? 0 : i - 1;
            int i1 = i + 1 > last ? last : i + 1;
            int i2 = i + 2 > last ? last : i + 2;
            l = Hermite(_l[im1], _l[i], _l[i1], _l[i2], x);
            r = Hermite(_r[im1], _r[i], _r[i1], _r[i2], x);
            double remaining = last - pos;
            if (remaining < FadeOutSource)
            {
                float g = (float)(remaining / FadeOutSource);
                g = g * g * (3f - 2f * g);
                l *= g; r *= g;
            }
        }

        if (_fade < FadeInSamples)
        {
            float g = (float)_fade / FadeInSamples;
            g = g * g * (3f - 2f * g);
            l *= g; r *= g;
            _fade++;
        }
        left = l; right = r;

        pos += _rate;
        if (_mode == SamplePlayMode.Loop)
        {
            double len = _len;
            if (pos >= len) { pos -= len; if (pos >= len) pos %= len; }
            else if (pos < 0) { pos += len; if (pos < 0) pos = ((pos % len) + len) % len; }
        }
        else if (pos >= _len - 1 || pos < 0)
        {
            _playing = false;
        }
        _pos = pos;
    }

    private static float Hermite(float ym1, float y0, float y1, float y2, float x)
    {
        float c1 = 0.5f * (y1 - ym1);
        float c2 = ym1 - 2.5f * y0 + 2f * y1 - 0.5f * y2;
        float c3 = 0.5f * (y2 - ym1) + 1.5f * (y0 - y1);
        return ((c3 * x + c2) * x + c1) * x + y0;
    }
}
