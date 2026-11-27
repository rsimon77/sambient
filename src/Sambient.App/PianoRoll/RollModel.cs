using Sambient.Seq;

namespace Sambient.App.PianoRoll;

/// <summary>Identity of a note in a track: a step can hold each pitch once, so (step, pitch) is unique.</summary>
public readonly record struct NoteKey(int Step, int Note);

/// <summary>A note as the piano roll sees it (step it starts on, pitch, velocity, tie length in steps).</summary>
public readonly record struct RollNote(int Step, int Note, int Velocity, int Tie)
{
    public NoteKey Key => new(Step, Note);

    /// <summary>Steps the bar covers on screen: the tie, cut at the track length for steps inside it (no wrap).</summary>
    public int VisibleTie(int length) => Step < length ? Math.Clamp(Math.Min(Tie, length - Step), 1, SeqLimits.MaxSteps) : Math.Max(1, Math.Min(Tie, SeqLimits.MaxSteps - Step));

    public static IReadOnlyList<RollNote> FromTrack(TrackSnapshot t)
    {
        var list = new List<RollNote>();
        for (int s = 0; s < t.Steps.Count; s++)
            foreach (var n in t.Steps[s].Notes.OrderBy(n => n.Note))
                list.Add(new RollNote(s, n.Note, n.Velocity, n.Tie));
        return list;
    }
}

/// <summary>A track as undo stores it: its steps (notes, probabilities, locks) and its length.</summary>
public sealed record TrackState(TrackData Data, int Length);

/// <summary>
/// Which MIDI notes the rows of the grid show: all 128 (row 0 = note 127 at the top) or, when folded, only the pitches that hold notes.
/// </summary>
public sealed class RowMap
{
    private readonly int[] _notes;
    private readonly Dictionary<int, int> _rowOf;
    public bool IsFolded { get; }
    public int Count => _notes.Length;

    private RowMap(int[] notes, bool folded)
    {
        _notes = notes; IsFolded = folded;
        _rowOf = new Dictionary<int, int>(notes.Length);
        for (int i = 0; i < notes.Length; i++) _rowOf[notes[i]] = i;
    }

    public static RowMap Full { get; } = new(Enumerable.Range(0, 128).Select(r => 127 - r).ToArray(), false);

    /// <summary>Only the given pitches (highest first). No pitches: the full range (a folded empty track would show nothing).</summary>
    public static RowMap Folded(IEnumerable<int> notes)
    {
        var arr = notes.Where(n => (uint)n < 128).Distinct().OrderByDescending(n => n).ToArray();
        return arr.Length == 0 ? Full : new RowMap(arr, true);
    }

    public int NoteOfRow(int row) => _notes[Math.Clamp(row, 0, _notes.Length - 1)];

    /// <summary>Row of a pitch, or -1 when the pitch is not shown (folded).</summary>
    public int RowOfNote(int note) => _rowOf.TryGetValue(note, out int r) ? r : -1;

    /// <summary>Row of a pitch, or of the nearest shown pitch.</summary>
    public int NearestRow(int note)
    {
        int r = RowOfNote(note);
        if (r >= 0) return r;
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < _notes.Length; i++)
        {
            int d = Math.Abs(_notes[i] - note);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}

/// <summary>Pitch classes of the scale that belongs to the layer's temperament and key (PR.MJ major, PR.MN natural minor); null = no scale.</summary>
public static class ScaleMap
{
    private static readonly int[] Major = [0, 2, 4, 5, 7, 9, 11];
    private static readonly int[] Minor = [0, 2, 3, 5, 7, 8, 10];

    public static bool[]? For(Sambient.Engine.Temperament t, int key)
    {
        int[]? degrees = t switch
        {
            Sambient.Engine.Temperament.PureMajor => Major,
            Sambient.Engine.Temperament.PureMinor => Minor,
            _ => null,
        };
        if (degrees == null) return null;
        var pcs = new bool[12];
        foreach (int d in degrees) pcs[(((d + key) % 12) + 12) % 12] = true;
        return pcs;
    }
}

/// <summary>Grid lines of the roll derived from the track's step length (NOTE parameter).</summary>
public static class RollGrid
{
    // quarter notes per step for each StepNote (same table the sequencer core uses)
    private static readonly double[] StepQuarters = [32, 16, 12, 8, 4, 2, 1.5, 1, 0.75, 4.0 / 3, 0.5, 0.375, 2.0 / 3, 0.25, 0.125];

    /// <summary>Steps per quarter-note beat (1/16 steps: 4, 1/8: 2, 1/4 and longer: 1).</summary>
    public static int StepsPerBeat(Sambient.Engine.StepNote n)
    {
        int i = Math.Clamp((int)n, 0, StepQuarters.Length - 1);
        return Math.Max(1, (int)Math.Round(1.0 / StepQuarters[i]));
    }

    /// <summary>Strength of the grid line before a step: 2 = bar (4 beats), 1 = beat, 0 = plain step.</summary>
    public static int LineStrength(int step, int stepsPerBeat)
        => step % (stepsPerBeat * 4) == 0 ? 2 : step % stepsPerBeat == 0 ? 1 : 0;
}
