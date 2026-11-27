namespace Sambient.App.ViewModels;

/// <summary>
/// UI-side mirror of the engine's HOLD (latched) notes, per layer. The keyboard shows the set of the displayed (selected)
/// layer. Not thread-safe: UI thread only.
/// </summary>
public sealed class LatchedNoteSet
{
    private readonly bool[][] _latched;
    private int _layer;

    public LatchedNoteSet(int layers) => _latched = Enumerable.Range(0, layers).Select(_ => new bool[128]).ToArray();

    /// <summary>Raised after any change, including a change of the displayed layer.</summary>
    public event Action? Changed;

    public int DisplayLayer
    {
        get => _layer;
        set { if (_layer != value) { _layer = value; Changed?.Invoke(); } }
    }

    public bool IsLatched(int note) => (uint)note < 128 && _latched[_layer][note];

    /// <summary>Flips a note on a layer and returns the new state.</summary>
    public bool Toggle(int layer, int note)
    {
        if ((uint)note >= 128) return false;
        bool v = _latched[layer][note] = !_latched[layer][note];
        Changed?.Invoke();
        return v;
    }

    public void Clear(int layer) { Array.Clear(_latched[layer]); Changed?.Invoke(); }

    public void ClearAll() { foreach (var l in _latched) Array.Clear(l); Changed?.Invoke(); }
}
