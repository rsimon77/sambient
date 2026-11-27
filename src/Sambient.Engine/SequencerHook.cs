namespace Sambient.Engine;

/// <summary>
/// What the engine offers a sequencer on the audio thread (implemented by <see cref="AudioEngine"/>). All methods are
/// audio-thread only and allocation-free.
/// </summary>
public interface ISequencerHost
{
    int SampleRate { get; }
    ParameterStore Parameters { get; }
    /// <summary>Sequencer note-on for a layer. Dropped when the layer is muted and <see cref="GlobalParamId.MuteMode"/> is SEQ.</summary>
    void SeqNoteOn(int layer, int note, int velocity);
    void SeqNoteOff(int layer, int note);
    /// <summary>Releases every voice of every layer (keys, holds and arpeggios included) with the layers' normal release.</summary>
    void ReleaseAllNotes();
    /// <summary>Clears the reverb tail (R.RST).</summary>
    void ResetReverb();
}

/// <summary>
/// A sequencer driven by <see cref="AudioEngine.Render"/>. The engine splits every render block at the sequencer's event
/// times: <c>BeginBlock</c>, then repeatedly { <c>ProcessDueEvents</c>; render min(rest, <c>SamplesToNextEvent</c>) frames;
/// <c>MixPost</c> on those frames; <c>Advance</c> }. Audio thread only; implementations must not allocate, lock or block.
/// </summary>
public interface IEngineSequencer
{
    /// <summary>Called once when the sequencer is attached (non-audio thread, before audio runs it).</summary>
    void Bind(ISequencerHost host);
    /// <summary>Start of every render call: drain commands from other threads.</summary>
    void BeginBlock(int frames);
    /// <summary>Fires every event that is due at the current position (note on/off, parameter locks, palette switches...).</summary>
    void ProcessDueEvents();
    /// <summary>Frames until the next event (0 = due now, int.MaxValue = nothing scheduled).</summary>
    int SamplesToNextEvent { get; }
    /// <summary>Post-master stage over the final stereo output of a chunk: relaxation fade, then the metronome click.</summary>
    void MixPost(Span<float> interleavedStereo, int frames);
    /// <summary>Moves time forward by <paramref name="frames"/> (after the chunk was rendered).</summary>
    void Advance(int frames);
}

/// <summary>Receives the notes the router has sent to a layer (used for sequencer recording). Called on the sending (UI / MIDI) thread.</summary>
public interface INoteListener
{
    void NoteOn(int layer, int note, int velocity);
    void NoteOff(int layer, int note);
}
