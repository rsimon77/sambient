namespace Sambient.Engine;

public enum NoteSource { Ui = 0, Midi = 1 }

/// <summary>
/// Routes note events to layers: by default to the SELECTED layer (<see cref="GlobalParamId.SelectedLayer"/>); optionally
/// (<see cref="MidiChannelToLayer"/>) MIDI channel 1..4 addresses layer 1..4. The layer that received a note-on is
/// remembered per source and note, so the matching note-off always goes to the same layer even if the selection changed
/// in between (no stuck notes). Thread-safe in the sense used by the app: each source is driven from one thread.
/// </summary>
public sealed class NoteRouter
{
    private readonly Func<AudioEngine> _engine;
    private readonly ParameterStore _params;
    private readonly int[] _layerOfNote = new int[2 * 128];      // layer + 1, 0 = not held

    public NoteRouter(Func<AudioEngine> engine, ParameterStore parameters)
    {
        _engine = engine; _params = parameters;
    }

    /// <summary>Optional observer of every routed note (the sequencer's recorder). Invoked on the calling thread after the engine call.</summary>
    public INoteListener? Listener { get; set; }

    /// <summary>Launchkey channel 1..4 -> layer 1..4 (off by default). Channels above 4 follow the selected layer.</summary>
    public bool MidiChannelToLayer { get; set; }

    /// <summary>0-based index of the selected layer.</summary>
    public int SelectedLayer => Math.Clamp((int)_params.Get(GlobalParamId.SelectedLayer) - 1, 0, ParameterStore.MaxLayers - 1);

    /// <summary>Sends a note-on to the target layer and returns its index. <paramref name="channel0"/>: 0-based MIDI channel or -1.</summary>
    public int NoteOn(NoteSource source, int note, int velocity, int channel0 = -1)
    {
        if ((uint)note >= 128) return -1;
        int layer = MidiChannelToLayer && source == NoteSource.Midi && channel0 is >= 0 and < ParameterStore.MaxLayers
            ? channel0 : SelectedLayer;
        int slot = (int)source * 128 + note;
        int prev = Volatile.Read(ref _layerOfNote[slot]) - 1;
        var e = _engine();
        if (prev >= 0 && prev != layer) e.NoteOff(prev, note);       // same key re-pressed on another layer
        Volatile.Write(ref _layerOfNote[slot], layer + 1);
        e.NoteOn(layer, note, velocity);
        Listener?.NoteOn(layer, note, velocity);
        return layer;
    }

    /// <summary>Sends the note-off to the layer that received the note-on; returns that layer (or -1 if the note was not held).</summary>
    public int NoteOff(NoteSource source, int note)
    {
        if ((uint)note >= 128) return -1;
        int layer = Interlocked.Exchange(ref _layerOfNote[(int)source * 128 + note], 0) - 1;
        if (layer < 0) return -1;
        _engine().NoteOff(layer, note);
        Listener?.NoteOff(layer, note);
        return layer;
    }

    /// <summary>Forgets all held notes (use together with <see cref="AudioEngine.AllNotesOff"/>).</summary>
    public void Clear() => Array.Clear(_layerOfNote);
}
