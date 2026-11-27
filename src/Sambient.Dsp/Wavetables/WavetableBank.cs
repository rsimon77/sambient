namespace Sambient.Dsp.Wavetables;

/// <summary>
/// Holds up to 32 WAVEs (index 1..32), built lazily on the thread pool. Built tables are published with
/// Volatile.Write after being fully constructed; readers (audio thread) never block.
/// Unknown or unimplemented indices resolve to SIN.S (index 3).
/// </summary>
public sealed class WavetableBank
{
    public const int MaxWaves = 32;
    public const int FallbackIndex = 3;

    private readonly Wavetable?[] _tables = new Wavetable?[MaxWaves + 1];
    private readonly Task?[] _tasks = new Task?[MaxWaves + 1];
    private readonly object _lock = new();

    /// <summary>Maps any int to the index actually served (the index itself if implemented, otherwise 3).</summary>
    public static int Resolve(int index) => WaveRecipes.IsImplemented(index) ? index : FallbackIndex;

    /// <summary>Starts (once) a background build of the resolved wave; returns the shared task. Non-RT.</summary>
    public Task EnsureBuiltAsync(int index)
    {
        int r = Resolve(index);
        lock (_lock)
        {
            return _tasks[r] ??= BackgroundBuilder.Run(() =>
            {
                WaveRecipes.TryGet(r, out var recipe);
                var table = WavetableBuilder.Build(recipe);
                Volatile.Write(ref _tables[r], table);
            });
        }
    }

    /// <summary>True when the wave that <see cref="Get"/> would return for this index is the exact (resolved) wave and built.</summary>
    public bool IsReady(int index) => Volatile.Read(ref _tables[Resolve(index)]) != null;

    /// <summary>
    /// Lock-free, allocation-free (safe on the audio thread). Returns the built table for the resolved index;
    /// if that is not built yet, SIN.S if it is built; otherwise null (call EnsureBuiltAsync(3) at startup to avoid this).
    /// </summary>
    public Wavetable? Get(int index)
    {
        var t = Volatile.Read(ref _tables[Resolve(index)]);
        return t ?? Volatile.Read(ref _tables[FallbackIndex]);
    }
}
