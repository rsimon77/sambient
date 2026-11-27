namespace Sambient.Dsp.Wavetables;

/// <summary>
/// A procedural definition of one WAVE. Stateless and thread-safe: <see cref="RenderCycle"/> may be called
/// concurrently for different frames. Adding a WAVE = adding one class and registering it in <see cref="WaveRecipes"/>.
/// </summary>
public interface IWaveRecipe
{
    /// <summary>Original WAVE list index, 1..32.</summary>
    int Index { get; }
    /// <summary>4-5 character display name, e.g. "SAW.S".</summary>
    string ShortName { get; }
    string Description { get; }

    /// <summary>
    /// Renders ONE period of the given frame (0..127 = HARMONIC value) naively in the time domain into
    /// <paramref name="cycle"/> (length = <see cref="Wavetable.CycleResolution"/>, sample i = phase i/length).
    /// Any harmonic content is allowed (the builder band-limits); DC and level are corrected by the builder.
    /// Must be smooth/continuous in <paramref name="frame"/>; frame 0 is the simplest, purest sound.
    /// </summary>
    void RenderCycle(int frame, Span<float> cycle);
}
