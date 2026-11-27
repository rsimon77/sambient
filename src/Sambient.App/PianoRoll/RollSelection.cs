using Avalonia;

namespace Sambient.App.PianoRoll;

/// <summary>The set of selected notes (by <see cref="NoteKey"/>) and the selection gestures.</summary>
public sealed class RollSelection
{
    private readonly HashSet<NoteKey> _keys = new();
    public int Count => _keys.Count;
    public IEnumerable<NoteKey> Keys => _keys;
    public bool Contains(NoteKey k) => _keys.Contains(k);

    public void Clear() => _keys.Clear();
    public void Set(IEnumerable<NoteKey> keys) { _keys.Clear(); foreach (var k in keys) _keys.Add(k); }
    public void Add(NoteKey k) => _keys.Add(k);
    public void Toggle(NoteKey k) { if (!_keys.Remove(k)) _keys.Add(k); }

    /// <summary>Selects every note inside the track length.</summary>
    public void SelectAll(IEnumerable<RollNote> notes, int length)
    {
        _keys.Clear();
        foreach (var n in notes) if (n.Step < length) _keys.Add(n.Key);
    }

    /// <summary>Drops keys of notes that no longer exist.</summary>
    public void Prune(IEnumerable<RollNote> notes)
    {
        var alive = new HashSet<NoteKey>(notes.Select(n => n.Key));
        _keys.RemoveWhere(k => !alive.Contains(k));
    }

    /// <summary>Notes whose bar touches the marquee rectangle (grid coordinates); past-length notes never match.</summary>
    public static IEnumerable<NoteKey> InRect(IEnumerable<RollNote> notes, RollViewport vp, int length, Rect marquee)
    {
        foreach (var n in notes)
        {
            if (n.Step >= length) continue;
            if (vp.NoteRect(n, length) is { } r && r.Intersects(marquee)) yield return n.Key;
        }
    }

    /// <summary>Marquee: replaces the selection with the hit notes, or - additive - unions them with the selection at gesture start.</summary>
    public void Marquee(IEnumerable<RollNote> notes, RollViewport vp, int length, Rect marquee, IReadOnlyCollection<NoteKey>? baseline)
    {
        _keys.Clear();
        if (baseline != null) foreach (var k in baseline) _keys.Add(k);
        foreach (var k in InRect(notes, vp, length, marquee)) _keys.Add(k);
    }
}
