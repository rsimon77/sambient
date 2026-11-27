namespace Sambient.Engine;

/// <summary>One arpeggiator event: a note starts (<see cref="IsNoteOn"/>) or its gate ends.</summary>
public struct ArpEvent
{
    public bool IsNoteOn;
    public int Note;
    public int Velocity;
}

/// <summary>
/// Sample-accurate arpeggiator core (audio thread, zero allocations, fixed-size arrays of <see cref="MaxNotes"/>).
/// Pure timing/ordering: it knows nothing about voices. The owner feeds it the held notes in press order
/// (<see cref="SetNotes"/>), calls <see cref="Start"/> when the first note arrives, and then, per audio block, loops:
/// <c>TryNextEvent</c> until false, render <c>min(blockRest, SamplesToNextEvent)</c> samples, <see cref="Advance"/>.
///
/// Clock: a double sample countdown carrying fractional samples, so a step of N.5 samples never drifts (error below 1
/// sample). A step = <see cref="StepQuarters"/> quarter notes at <see cref="Bpm"/>; the note sounds for
/// <see cref="Gate"/> of the step. The clock is free-running (it is not reset when notes are added while running).
///
/// Orders for held notes sorted ascending a0..a(k-1) (k = count): UP a; DOWN reversed; U.D up then down without
/// repeating the ends (C E G E); D.U (G E C E); U.A.D repeating the ends (C E G G E C); D.A.U (G E C C E G);
/// UP+1 / UP+2: up through the notes, then again 1 / 2 octaves higher (C E G C' E' G'); DN-1 / DN-2: down through the
/// notes, then 1 / 2 octaves lower; P.O: press order; RNDM: a random held note per step. Transposed notes outside 0..127
/// are dropped.
/// </summary>
public sealed class Arpeggiator
{
    public const int MaxNotes = 32;
    private const int MaxSeq = MaxNotes * 3 + 8;

    /// <summary>Quarter-note length of each <see cref="StepNote"/> value (indexed by the enum).</summary>
    public static readonly float[] StepQuarterNotes =
    {
        32f, 16f, 12f, 8f, 4f, 2f, 1.5f, 1f, 0.75f, 4f / 3f, 0.5f, 0.375f, 2f / 3f, 0.25f, 0.125f,
    };

    private readonly int[] _notes = new int[MaxNotes];
    private readonly int[] _vels = new int[MaxNotes];
    private readonly int[] _seqNotes = new int[MaxSeq];
    private readonly int[] _seqVels = new int[MaxSeq];
    private int _count;
    private bool _running, _gateOn;
    private double _toNext;
    private int _step;
    private int _curNote, _curVel;
    private uint _rng = 0x9E3779B9u;

    public ArpType Type { get; set; } = ArpType.Up;
    public float StepQuarters { get; set; } = 0.25f;
    public float Bpm { get; set; } = 120f;
    public double SampleRate { get; set; } = 48000.0;
    /// <summary>Gate length as a fraction of the step (0.5..0.8 sounds right).</summary>
    public float Gate { get; set; } = 0.6f;
    /// <summary>Note probability 0..1 (DICE hook; 1 = every step plays).</summary>
    public float Probability { get; set; } = 1f;
    public bool Running => _running;
    public bool GateOn => _gateOn;
    public int Count => _count;

    /// <summary>Step length in (fractional) samples.</summary>
    public double StepSamples => StepQuarters * 60.0 / Math.Max(1f, Bpm) * SampleRate;

    public void Seed(uint seed) => _rng = seed == 0 ? 1u : seed;

    /// <summary>Sets the held notes (press order, oldest first) and their velocities.</summary>
    public void SetNotes(ReadOnlySpan<int> notes, ReadOnlySpan<int> velocities)
    {
        int n = Math.Min(Math.Min(notes.Length, velocities.Length), MaxNotes);
        for (int i = 0; i < n; i++) { _notes[i] = notes[i]; _vels[i] = velocities[i]; }
        _count = n;
    }

    /// <summary>Starts the clock: the first note fires at the next <see cref="TryNextEvent"/>.</summary>
    public void Start()
    {
        _running = true; _gateOn = false; _toNext = 0; _step = 0;
    }

    public void Stop()
    {
        _running = false; _gateOn = false; _toNext = 0;
    }

    /// <summary>Samples until the next event (at least 1 after all due events were consumed); int.MaxValue when stopped.</summary>
    public int SamplesToNextEvent
    {
        get
        {
            if (!_running) return int.MaxValue;
            double t = Math.Ceiling(_toNext);
            return t <= 0 ? 0 : t >= int.MaxValue ? int.MaxValue : (int)t;
        }
    }

    public void Advance(int samples)
    {
        if (_running) _toNext -= samples;
    }

    /// <summary>Returns the next due event (note-on or gate-off) if the countdown has elapsed.</summary>
    public bool TryNextEvent(out ArpEvent ev)
    {
        ev = default;
        if (!_running || _toNext > 1e-9) return false;
        double step = StepSamples;
        if (!_gateOn)
        {
            int len = Build(Type, _notes.AsSpan(0, _count), _vels.AsSpan(0, _count), _seqNotes, _seqVels);
            if (len == 0) { _toNext += step; return false; }
            int idx;
            if (Type == ArpType.Random) idx = (int)(NextRandom() % (uint)len);
            else idx = _step % len;
            _step = idx + 1;
            _toNext += step * Gate;
            bool play = Probability >= 1f || (NextRandom() & 0xFFFFFF) * (1.0 / 0x1000000) < Probability;
            if (!play) { _toNext += step * (1.0 - Gate); return TryNextEvent(out ev); }
            _gateOn = true;
            _curNote = _seqNotes[idx]; _curVel = _seqVels[idx];
            ev = new ArpEvent { IsNoteOn = true, Note = _curNote, Velocity = _curVel };
            return true;
        }
        _gateOn = false;
        _toNext += step * (1.0 - Gate);
        ev = new ArpEvent { IsNoteOn = false, Note = _curNote, Velocity = 0 };
        return true;
    }

    private uint NextRandom()
    {
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return _rng = x;
    }

    /// <summary>
    /// Builds the note order for <paramref name="type"/> from the held notes (press order). Returns the length written to
    /// <paramref name="outNotes"/>/<paramref name="outVels"/> (each at least 3 * 32 + 8 long). RNDM returns the ascending set.
    /// </summary>
    public static int Build(ArpType type, ReadOnlySpan<int> held, ReadOnlySpan<int> vels, Span<int> outNotes, Span<int> outVels)
    {
        int k = Math.Min(held.Length, MaxNotes);
        if (k == 0) return 0;
        Span<int> ord = stackalloc int[MaxNotes];          // indices into held, ascending by note
        for (int i = 0; i < k; i++)
        {
            int j = i;
            while (j > 0 && held[ord[j - 1]] > held[i]) { ord[j] = ord[j - 1]; j--; }
            ord[j] = i;
        }
        int len = 0;
        ReadOnlySpan<int> o = ord.Slice(0, k);
        switch (type)
        {
            case ArpType.Up: case ArpType.Random: Run(held, vels, o, 0, false, 0, k, outNotes, outVels, ref len); break;
            case ArpType.Down: Run(held, vels, o, 0, true, 0, k, outNotes, outVels, ref len); break;
            case ArpType.UpDown:
                Run(held, vels, o, 0, false, 0, k, outNotes, outVels, ref len);
                Run(held, vels, o, 0, true, 1, k - 1, outNotes, outVels, ref len);
                break;
            case ArpType.DownUp:
                Run(held, vels, o, 0, true, 0, k, outNotes, outVels, ref len);
                Run(held, vels, o, 0, false, 1, k - 1, outNotes, outVels, ref len);
                break;
            case ArpType.UpAndDown:
                Run(held, vels, o, 0, false, 0, k, outNotes, outVels, ref len);
                Run(held, vels, o, 0, true, 0, k, outNotes, outVels, ref len);
                break;
            case ArpType.DownAndUp:
                Run(held, vels, o, 0, true, 0, k, outNotes, outVels, ref len);
                Run(held, vels, o, 0, false, 0, k, outNotes, outVels, ref len);
                break;
            case ArpType.UpPlus1:
            case ArpType.UpPlus2:
                for (int oct = 0; oct <= (type == ArpType.UpPlus1 ? 1 : 2); oct++)
                    Run(held, vels, o, 12 * oct, false, 0, k, outNotes, outVels, ref len);
                break;
            case ArpType.DownMinus1:
            case ArpType.DownMinus2:
                for (int oct = 0; oct <= (type == ArpType.DownMinus1 ? 1 : 2); oct++)
                    Run(held, vels, o, -12 * oct, true, 0, k, outNotes, outVels, ref len);
                break;
            case ArpType.PlayOrder:
                for (int i = 0; i < k; i++) Emit(held, vels, i, 0, outNotes, outVels, ref len);
                break;
        }
        return len;
    }

    private static void Emit(ReadOnlySpan<int> held, ReadOnlySpan<int> vels, int idx, int transpose,
        Span<int> outNotes, Span<int> outVels, ref int len)
    {
        int n = held[idx] + transpose;
        if ((uint)n > 127 || len >= outNotes.Length) return;
        outNotes[len] = n; outVels[len] = vels[idx]; len++;
    }

    /// <summary>Emits sorted positions [from, to) ascending or descending (descending walks to-1 .. from).</summary>
    private static void Run(ReadOnlySpan<int> held, ReadOnlySpan<int> vels, ReadOnlySpan<int> ord, int transpose, bool descending,
        int from, int to, Span<int> outNotes, Span<int> outVels, ref int len)
    {
        if (descending) for (int i = to - 1; i >= from; i--) Emit(held, vels, ord[i], transpose, outNotes, outVels, ref len);
        else for (int i = from; i < to; i++) Emit(held, vels, ord[i], transpose, outNotes, outVels, ref len);
    }
}
