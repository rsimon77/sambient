namespace Sambient.Engine;

/// <summary>
/// UI-side 128-note state: which notes are held or (with the sustain pedal down) still sounding because of the pedal.
/// Fed from the same note events that go to the engine (any source). Not thread-safe: use from the UI thread.
/// </summary>
public sealed class HeldNoteTracker
{
    private readonly bool[] _held = new bool[128];
    private readonly bool[] _sustained = new bool[128];
    private bool _sustain;

    /// <summary>Raised after any change.</summary>
    public event Action? Changed;

    public bool IsSounding(int note) => (uint)note < 128 && (_held[note] || _sustained[note]);
    public bool IsHeld(int note) => (uint)note < 128 && _held[note];
    public bool SustainDown => _sustain;

    public void NoteOn(int note)
    {
        if ((uint)note >= 128) return;
        _held[note] = true; _sustained[note] = false;
        Changed?.Invoke();
    }

    public void NoteOff(int note)
    {
        if ((uint)note >= 128) return;
        _held[note] = false;
        if (_sustain) _sustained[note] = true;
        Changed?.Invoke();
    }

    public void SetSustain(bool down)
    {
        _sustain = down;
        if (!down) Array.Clear(_sustained);
        Changed?.Invoke();
    }

    public void Clear()
    {
        Array.Clear(_held); Array.Clear(_sustained);
        Changed?.Invoke();
    }
}
