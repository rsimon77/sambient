using Sambient.Seq;

namespace Sambient.App.PianoRoll;

/// <summary>Pure helpers that turn piano-roll gestures into note edits for <see cref="NoteEditing"/> / the sequencer.</summary>
public static class RollOps
{
    /// <summary>Limits a move offset so that no selected note leaves steps 0..length-1 or pitches 0..127.</summary>
    public static (int dStep, int dNote) ClampDelta(IReadOnlyCollection<RollNote> sel, int length, int dStep, int dNote)
    {
        if (sel.Count == 0) return (0, 0);
        int minStep = sel.Min(n => n.Step), maxStep = sel.Max(n => n.Step);
        int minNote = sel.Min(n => n.Note), maxNote = sel.Max(n => n.Note);
        dStep = Math.Clamp(dStep, -minStep, Math.Max(-minStep, length - 1 - maxStep));
        dNote = Math.Clamp(dNote, -minNote, 127 - maxNote);
        return (dStep, dNote);
    }

    public static List<NoteEdit> MoveEdits(IEnumerable<RollNote> sel, int dStep, int dNote)
        => sel.Select(n => NoteEdit.Move(n.Step, n.Note, n.Step + dStep, n.Note + dNote)).ToList();

    public static List<NoteEdit> RemoveEdits(IEnumerable<NoteKey> keys) => keys.Select(k => NoteEdit.Remove(k.Step, k.Note)).ToList();

    /// <summary>The selection after a batch of moves: moved notes follow their move, blocked ones keep their place.</summary>
    public static List<NoteKey> SelectionAfterMove(IReadOnlyList<NoteEdit> edits, NoteEditResult result)
    {
        var keys = new List<NoteKey>(edits.Count);
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (e.Kind != NoteEditKind.Move) continue;
            keys.Add(result.Succeeded[i] ? new NoteKey(e.ToStep, e.ToNote) : new NoteKey(e.Step, e.Note));
        }
        return keys;
    }

    /// <summary>
    /// Tie length for a bar that starts at <paramref name="step"/> when its end is dragged to the grid line nearest to
    /// <paramref name="stepPos"/> (a fractional step position): at least 1, never past the track end.
    /// </summary>
    public static int TieFromPosition(int step, double stepPos, int length)
        => Math.Clamp((int)Math.Round(stepPos, MidpointRounding.AwayFromZero) - step, 1, Math.Max(1, length - step));

    /// <summary>Tie edits for resizing a selection: every note gets the same change as the grabbed one (clamped by the sequencer).</summary>
    public static List<NoteEdit> ResizeEdits(IEnumerable<RollNote> sel, int delta)
        => sel.Select(n => NoteEdit.SetTie(n.Step, n.Note, n.Tie + delta)).ToList();

    /// <summary>Velocity from a vertical position inside the lane: top = 127, bottom = 1.</summary>
    public static int VelocityFromY(double y, double laneTop, double laneHeight)
    {
        double f = 1 - Math.Clamp((y - laneTop) / Math.Max(1, laneHeight), 0, 1);
        return Math.Clamp((int)Math.Round(1 + f * 126), 1, 127);
    }

    /// <summary>Clipboard content: notes relative to the first step of the copy.</summary>
    public sealed record Clip(IReadOnlyList<RollNote> Notes)
    {
        public static Clip? From(IEnumerable<RollNote> notes)
        {
            var arr = notes.OrderBy(n => n.Step).ThenBy(n => n.Note).ToArray();
            if (arr.Length == 0) return null;
            int first = arr[0].Step;
            return new Clip(arr.Select(n => n with { Step = n.Step - first }).ToArray());
        }
    }

    /// <summary>Add edits that paste a clip with its first step at <paramref name="atStep"/>; notes that would start past the track end are dropped.</summary>
    public static List<NoteEdit> PasteEdits(Clip clip, int atStep, int length)
        => clip.Notes.Where(n => atStep + n.Step < length)
            .Select(n => NoteEdit.Add(atStep + n.Step, n.Note, n.Velocity, n.Tie)).ToList();
}
