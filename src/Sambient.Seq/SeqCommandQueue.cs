namespace Sambient.Seq;

internal enum CmdType : byte { Play, Stop, StopNatural, Load, Adopt, Queue, SetChain, ClearChain, LiveParam, Seed }

internal struct SeqCommand
{
    public CmdType Type;
    public int A, B;
    public float F;
    public PreparedPalette? Palette;
    public ChainPlan? Chain;
}

/// <summary>Bounded multi-producer / single-consumer lock-free queue (same algorithm as <c>EngineEventQueue</c>), generic over a command struct.</summary>
internal sealed class MpscQueue<T> where T : struct
{
    private struct Slot { public long Seq; public T Value; }
    private readonly Slot[] _slots;
    private readonly long _mask;
    private long _tail, _head, _dropped;

    public MpscQueue(int capacity)
    {
        _slots = new Slot[capacity];
        _mask = capacity - 1;
        for (int i = 0; i < capacity; i++) _slots[i].Seq = i;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(in T v)
    {
        long pos = Volatile.Read(ref _tail);
        while (true)
        {
            ref Slot s = ref _slots[pos & _mask];
            long dif = Volatile.Read(ref s.Seq) - pos;
            if (dif == 0)
            {
                long prev = Interlocked.CompareExchange(ref _tail, pos + 1, pos);
                if (prev == pos) { s.Value = v; Volatile.Write(ref s.Seq, pos + 1); return true; }
                pos = prev;
            }
            else if (dif < 0) { Interlocked.Increment(ref _dropped); return false; }
            else pos = Volatile.Read(ref _tail);
        }
    }

    public bool TryDequeue(out T v)
    {
        long pos = _head;
        ref Slot s = ref _slots[pos & _mask];
        if (Volatile.Read(ref s.Seq) - (pos + 1) < 0) { v = default; return false; }
        v = s.Value;
        Volatile.Write(ref s.Seq, pos + _mask + 1);
        _head = pos + 1;
        return true;
    }
}
