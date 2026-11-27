using Sambient.Engine;

namespace Sambient.Seq;

internal readonly record struct PatNote(byte Note, byte Velocity, ushort Tie);

/// <summary>
/// Immutable, flat, audio-thread-readable form of the four sequences. Step index = track * 64 + step. Built on the UI thread
/// (<see cref="Compile"/>), published by reference swap, never modified afterwards.
/// </summary>
public sealed class CompiledPattern
{
    internal const int Total = SeqLimits.Tracks * SeqLimits.MaxSteps;
    internal readonly byte[] NoteCount = new byte[Total];
    internal readonly byte[] Probability = new byte[Total];
    internal readonly ushort[] NoteStart = new ushort[Total];
    internal readonly ushort[] LockStart = new ushort[Total];
    internal readonly byte[] LockCount = new byte[Total];
    internal PatNote[] Notes = [];
    internal ParamLock[] Locks = [];
    public int PatternBars { get; internal set; }

    public static CompiledPattern Compile(PatternPalette pal)
    {
        var c = new CompiledPattern { PatternBars = Math.Clamp(pal.PatternBars, 0, 512) };
        var notes = new List<PatNote>();
        var locks = new List<ParamLock>();
        for (int t = 0; t < SeqLimits.Tracks; t++)
            for (int s = 0; s < SeqLimits.MaxSteps; s++)
            {
                int f = t * SeqLimits.MaxSteps + s;
                var st = pal.Tracks[t].Steps[s];
                c.Probability[f] = (byte)Math.Clamp(st.Probability, SeqLimits.MinProbability, 100);
                c.NoteStart[f] = (ushort)notes.Count;
                int n = Math.Min(st.Notes.Count, SeqLimits.MaxNotesPerStep);
                c.NoteCount[f] = (byte)n;
                for (int i = 0; i < n; i++)
                {
                    var d = st.Notes[i];
                    notes.Add(new PatNote((byte)Math.Clamp(d.Note, 0, 127), (byte)Math.Clamp(d.Velocity, 1, 127), (ushort)Math.Clamp(d.Tie, 1, SeqLimits.MaxSteps)));
                }
                c.LockStart[f] = (ushort)locks.Count;
                int lc = 0;
                foreach (var kv in st.Locks)
                    if (LockRules.IsLockable(kv.Key)) { locks.Add(new ParamLock(kv.Key, kv.Value)); lc++; }
                c.LockCount[f] = (byte)lc;
            }
        c.Notes = notes.ToArray();
        c.Locks = locks.ToArray();
        return c;
    }

    public static CompiledPattern Empty { get; } = Compile(new PatternPalette());
}

/// <summary>A palette ready for the audio thread: flat base values to load and the (replaceable) compiled sequences.</summary>
public sealed class PreparedPalette
{
    /// <summary>The working copy the UI edits (edits republish <see cref="Pattern"/>).</summary>
    public PatternPalette Model { get; }
    /// <summary>Bank slot (bank * 16 + slot) the palette was loaded from, or -1.</summary>
    public int Slot { get; internal set; }
    internal readonly float[] LayerBase;
    internal readonly float[] GlobalBase;
    private CompiledPattern _pattern;
    internal CompiledPattern Pattern { get => Volatile.Read(ref _pattern); set => Volatile.Write(ref _pattern, value); }

    public PreparedPalette(PatternPalette model, int slot)
    {
        Model = model; Slot = slot;
        int n = ParamTable.LayerParamCount;
        LayerBase = new float[SeqLimits.Tracks * n];
        for (int l = 0; l < SeqLimits.Tracks; l++) Array.Copy(model.Layers[l], 0, LayerBase, l * n, n);
        GlobalBase = (float[])model.Globals.Clone();
        _pattern = CompiledPattern.Compile(model);
    }

    public void Republish() => Pattern = CompiledPattern.Compile(Model);
}

/// <summary>Chain playback plan: palettes played in order, each <see cref="Repeats"/> pattern cycles; optional loop and Relaxation fade.</summary>
public sealed class ChainPlan
{
    public required PreparedPalette[] Items { get; init; }
    public bool Loop { get; init; }
    /// <summary>Pattern cycles per palette (1..99; Relaxation: 2..10).</summary>
    public int Repeats { get; init; } = 1;
    /// <summary>Relaxation mode: after <see cref="Repeats"/> cycles fade out over <see cref="FadeSeconds"/>, then start the next palette (fading in).</summary>
    public bool Relaxation { get; init; }
    /// <summary>Fade-out time; the hardware offers 5/10/15/20/25/30 s.</summary>
    public int FadeSeconds { get; init; } = 10;
    /// <summary>Fade-in time after the switch; 0 = same as the fade-out time.</summary>
    public int FadeInSeconds { get; init; }
}
