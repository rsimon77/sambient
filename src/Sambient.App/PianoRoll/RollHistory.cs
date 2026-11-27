namespace Sambient.App.PianoRoll;

/// <summary>
/// Undo / redo of piano-roll edits: one stack pair per track holding whole-track snapshots (at most <see cref="MaxDepth"/> undo
/// steps each). Usage: <see cref="Push"/> the state BEFORE an edit; <see cref="Undo"/> / <see cref="Redo"/> take the current state
/// and return the state to restore (null when there is nothing to do).
/// </summary>
public sealed class RollHistory
{
    public const int MaxDepth = 50;
    private readonly List<TrackState>[] _undo = Enumerable.Range(0, 8).Select(_ => new List<TrackState>()).ToArray();
    private readonly List<TrackState>[] _redo = Enumerable.Range(0, 8).Select(_ => new List<TrackState>()).ToArray();

    public bool CanUndo(int track) => _undo[track].Count > 0;
    public bool CanRedo(int track) => _redo[track].Count > 0;
    public int UndoDepth(int track) => _undo[track].Count;

    public void Push(int track, TrackState before)
    {
        var u = _undo[track];
        u.Add(before);
        if (u.Count > MaxDepth) u.RemoveAt(0);
        _redo[track].Clear();
    }

    public TrackState? Undo(int track, TrackState current)
    {
        var u = _undo[track];
        if (u.Count == 0) return null;
        var prev = u[^1]; u.RemoveAt(u.Count - 1);
        _redo[track].Add(current);
        return prev;
    }

    public TrackState? Redo(int track, TrackState current)
    {
        var r = _redo[track];
        if (r.Count == 0) return null;
        var next = r[^1]; r.RemoveAt(r.Count - 1);
        _undo[track].Add(current);
        return next;
    }

    /// <summary>Forgets everything (another palette was loaded).</summary>
    public void Clear() { foreach (var l in _undo) l.Clear(); foreach (var l in _redo) l.Clear(); }
}
