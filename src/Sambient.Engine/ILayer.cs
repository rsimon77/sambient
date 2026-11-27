namespace Sambient.Engine;

/// <summary>A sound-producing layer of the engine (synth or noise). Audio-thread only.</summary>
public interface ILayer
{
    int Index { get; }
    int ActiveVoices { get; }
    void NoteOn(int note, int velocity);
    void NoteOff(int note);
    void AllNotesOff();
    void SetSustain(bool down);
    void SetPitchBend(float value);
    void SetModWheel(float value);
    /// <summary>Renders <paramref name="frames"/> frames and ADDS them (after level and pan) to the interleaved stereo buffer.</summary>
    void Render(Span<float> interleavedStereo, int frames);
}
