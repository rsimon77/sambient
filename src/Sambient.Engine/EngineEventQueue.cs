namespace Sambient.Engine;

public enum EngineEventType : byte { NoteOn, NoteOff, AllNotesOff, PitchBend, ModWheel, Sustain }

/// <summary>
/// One event for the audio thread. NoteOn velocity 0 means "use the layer's fixed Velocity parameter".
/// <see cref="Value"/>: pitch bend -1..1, mod wheel 0..1, sustain 0/1.
/// </summary>
public struct EngineEvent
{
    public EngineEventType Type;
    public byte Layer;
    public byte Note;
    public byte Velocity;
    public float Value;
}

/// <summary>
/// Bounded multi-producer / single-consumer lock-free queue (Vyukov bounded queue: a per-slot sequence number plus an
/// Interlocked-reserved tail). Fixed capacity (power of two), never allocates after construction; when full the event
/// is dropped and counted in <see cref="Dropped"/>. Producers: any thread. Consumer: the audio thread only.
/// </summary>
public sealed class EngineEventQueue
{
    private struct Slot
    {
        public long Seq;
        public EngineEvent Ev;
    }

    private readonly Slot[] _slots;
    private readonly long _mask;
    private long _tail;       // producers (Interlocked)
    private long _head;       // consumer only
    private long _dropped;

    public EngineEventQueue(int capacity = 1024)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0) throw new ArgumentException("capacity must be a power of two", nameof(capacity));
        _slots = new Slot[capacity];
        _mask = capacity - 1;
        for (int i = 0; i < capacity; i++) _slots[i].Seq = i;
    }

    public int Capacity => _slots.Length;

    /// <summary>Number of events dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Thread-safe for any number of producers. Returns false (and counts a drop) when full.</summary>
    public bool TryEnqueue(in EngineEvent ev)
    {
        long pos = Volatile.Read(ref _tail);
        while (true)
        {
            ref Slot s = ref _slots[pos & _mask];
            long seq = Volatile.Read(ref s.Seq);
            long dif = seq - pos;
            if (dif == 0)
            {
                long prev = Interlocked.CompareExchange(ref _tail, pos + 1, pos);
                if (prev == pos)
                {
                    s.Ev = ev;
                    Volatile.Write(ref s.Seq, pos + 1);   // publish
                    return true;
                }
                pos = prev;
            }
            else if (dif < 0)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            else
            {
                pos = Volatile.Read(ref _tail);
            }
        }
    }

    /// <summary>Single consumer only (audio thread).</summary>
    public bool TryDequeue(out EngineEvent ev)
    {
        long pos = _head;
        ref Slot s = ref _slots[pos & _mask];
        long seq = Volatile.Read(ref s.Seq);
        if (seq - (pos + 1) < 0)
        {
            ev = default;
            return false;
        }
        ev = s.Ev;
        Volatile.Write(ref s.Seq, pos + _mask + 1);   // free the slot for the producer one lap later
        _head = pos + 1;
        return true;
    }
}
