namespace Sambient.Seq;

public enum NoteEditKind { Add, Remove, Move, SetTie, SetVelocity }

/// <summary>
/// One note-level edit of a batch (<see cref="Sequencer.ApplyNoteEdits"/>). <see cref="Step"/> / <see cref="Note"/> address the note
/// (or, for Add, the place it is created); Move relocates it to <see cref="ToStep"/> / <see cref="ToNote"/>.
/// Use the factory methods.
/// </summary>
public readonly record struct NoteEdit(NoteEditKind Kind, int Step, int Note, int ToStep = 0, int ToNote = 0, int Velocity = 100, int Tie = 1)
{
    public static NoteEdit Add(int step, int note, int velocity = 100, int tie = 1) => new(NoteEditKind.Add, step, note, Velocity: velocity, Tie: tie);
    public static NoteEdit Remove(int step, int note) => new(NoteEditKind.Remove, step, note);
    public static NoteEdit Move(int step, int note, int toStep, int toNote) => new(NoteEditKind.Move, step, note, toStep, toNote);
    public static NoteEdit SetTie(int step, int note, int tie) => new(NoteEditKind.SetTie, step, note, Tie: tie);
    public static NoteEdit SetVelocity(int step, int note, int velocity) => new(NoteEditKind.SetVelocity, step, note, Velocity: velocity);
}

/// <summary>Outcome of a batch: one flag per submitted edit (same order).</summary>
public sealed record NoteEditResult(IReadOnlyList<bool> Succeeded)
{
    public int Applied => Succeeded.Count(s => s);
    public int Failed => Succeeded.Count - Applied;
    public bool AllSucceeded => Failed == 0;
}

/// <summary>
/// The pure (lock-free, audio-free) note editing rules shared by the single-note and batch operations of the sequencer.
///
/// BATCH SEMANTICS. The batch is applied to a working copy in phases, then the touched steps replace the originals at once:
/// <list type="number">
/// <item>SetTie / SetVelocity change the addressed notes in place (they see the notes as they were before the batch moved anything).</item>
/// <item>Remove deletes notes; every Move lifts its note out of its source step. Because all sources leave first, a group can
/// shift onto the places the group itself vacates (move every note one step to the right).</item>
/// <item>Moves are then placed (in order), then Adds. A placement keeps velocity / tie of the note and clamps the tie so it does
/// not pass the track end.</item>
/// </list>
/// CONFLICTS. A Move FAILS (its note stays exactly where it was) when its source note does not exist, the target step or pitch is
/// out of range, the target step already holds 8 other notes, or the target already holds a note of that pitch (from outside the
/// batch or placed by an earlier move). Moving a note onto its own place succeeds as a no-op. A failed move pins its note at the
/// source, which can in turn block other moves; this is resolved by repeating until stable, so the result is deterministic and
/// never loses or duplicates a note. Add inserts a note, or re-velocities / re-ties one of the same pitch (like
/// <see cref="Sequencer.AddStepNote"/>), and fails only when the step is full. Remove / SetTie / SetVelocity fail when the note is missing.
/// Targets are checked against the 64 step limit, not against the track length (steps past the length are simply inactive).
/// </summary>
public static class NoteEditing
{
    private static bool ValidStep(int s) => (uint)s < SeqLimits.MaxSteps;
    private static bool ValidNote(int n) => (uint)n < 128;

    private static int ClampTie(int length, int step, int tie) => Math.Clamp(tie, 1, Math.Max(1, length - step));

    /// <summary>Applies <paramref name="edits"/> to <paramref name="track"/> (modified in place; steps that change are replaced by new instances).</summary>
    public static NoteEditResult Apply(TrackData track, int length, IReadOnlyList<NoteEdit> edits)
    {
        var ok = new bool[edits.Count];
        var pinned = new HashSet<int>();                 // indices of failed moves (their notes stay at the source)
        StepData[] work;
        while (true)
        {
            work = track.Steps.Select(s => s.Clone()).ToArray();
            Array.Clear(ok);
            int newlyFailed = Run(work, length, edits, pinned, ok);
            if (newlyFailed == 0) break;
        }
        for (int i = 0; i < work.Length; i++)
            if (!SameNotes(track.Steps[i], work[i])) track.Steps[i] = work[i];
        return new NoteEditResult(ok);
    }

    private static bool SameNotes(StepData a, StepData b) => a.Notes.SequenceEqual(b.Notes);

    private static int FindNote(StepData s, int note) => s.Notes.FindIndex(n => n.Note == note);

    /// <summary>One pass over the working copy. Returns how many moves failed that were not pinned before (they get pinned by the caller).</summary>
    private static int Run(StepData[] work, int length, IReadOnlyList<NoteEdit> edits, HashSet<int> pinned, bool[] ok)
    {
        // phase 1a: tie / velocity in place
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (e.Kind is not (NoteEditKind.SetTie or NoteEditKind.SetVelocity)) continue;
            if (!ValidStep(e.Step)) continue;
            int idx = FindNote(work[e.Step], e.Note);
            if (idx < 0) continue;
            var n = work[e.Step].Notes[idx];
            work[e.Step].Notes[idx] = e.Kind == NoteEditKind.SetTie
                ? n with { Tie = ClampTie(length, e.Step, e.Tie) }
                : n with { Velocity = Math.Clamp(e.Velocity, 1, 127) };
            ok[i] = true;
        }
        // phase 1b: removes, move sources
        var lifted = new Dictionary<int, StepNoteData>();
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (e.Kind == NoteEditKind.Remove)
            {
                if (!ValidStep(e.Step)) continue;
                int idx = FindNote(work[e.Step], e.Note);
                if (idx < 0) continue;
                work[e.Step].Notes.RemoveAt(idx);
                ok[i] = true;
            }
            else if (e.Kind == NoteEditKind.Move && !pinned.Contains(i))
            {
                if (!ValidStep(e.Step) || !ValidStep(e.ToStep) || !ValidNote(e.Note) || !ValidNote(e.ToNote)) { pinned.Add(i); continue; }
                int idx = FindNote(work[e.Step], e.Note);
                if (idx < 0) { pinned.Add(i); continue; }
                lifted[i] = work[e.Step].Notes[idx];
                work[e.Step].Notes.RemoveAt(idx);
            }
        }
        // phase 2: place moves, then adds
        int newlyFailed = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (e.Kind != NoteEditKind.Move || !lifted.TryGetValue(i, out var data)) continue;
            var target = work[e.ToStep];
            bool same = e.ToStep == e.Step && e.ToNote == e.Note;
            if (!same && (FindNote(target, e.ToNote) >= 0 || target.Notes.Count >= SeqLimits.MaxNotesPerStep))
            {
                pinned.Add(i); newlyFailed++;
                continue;
            }
            target.Notes.Add(data with { Note = e.ToNote, Tie = ClampTie(length, e.ToStep, data.Tie) });
            ok[i] = true;
        }
        if (newlyFailed > 0) return newlyFailed;
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (e.Kind != NoteEditKind.Add || !ValidStep(e.Step) || !ValidNote(e.Note)) continue;
            var st = work[e.Step];
            var nd = StepNoteData.Make(e.Note, e.Velocity, ClampTie(length, e.Step, e.Tie));
            int idx = FindNote(st, e.Note);
            if (idx >= 0) { st.Notes[idx] = nd; ok[i] = true; }
            else if (st.Notes.Count < SeqLimits.MaxNotesPerStep) { st.Notes.Add(nd); ok[i] = true; }
        }
        return 0;
    }
}
