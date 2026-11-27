using Sambient.Engine;

namespace Sambient.Seq;

/// <summary>
/// A deterministic 64-bit content hash (FNV-1a) of a palette: name, bar count, every layer parameter, the pattern globals and every
/// step (notes, probability, locks). Used to tell whether a palette was edited (dirty tracking) or still equals its factory default.
/// </summary>
public static class PatternFingerprint
{
    private const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

    private static void Mix(ref ulong h, int v)
    {
        unchecked
        {
            for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (i * 8)); h *= Prime; }
        }
    }

    private static void Mix(ref ulong h, float f) => Mix(ref h, f == 0f ? 0 : BitConverter.SingleToInt32Bits(f));

    public static ulong Of(PatternPalette p)
    {
        ulong h = Offset;
        foreach (char c in p.Name) Mix(ref h, c);
        Mix(ref h, p.PatternBars);
        for (int l = 0; l < SeqLimits.Tracks; l++)
            foreach (float v in p.Layers[l]) Mix(ref h, v);
        foreach (var g in PatternPalette.PatternGlobals) Mix(ref h, p.Globals[(int)g]);
        for (int t = 0; t < SeqLimits.Tracks; t++)
            for (int s = 0; s < SeqLimits.MaxSteps; s++)
            {
                var st = p.Tracks[t].Steps[s];
                if (st.IsEmpty) continue;
                Mix(ref h, t * SeqLimits.MaxSteps + s);
                Mix(ref h, st.Probability);
                Mix(ref h, st.Notes.Count);
                foreach (var n in st.Notes) { Mix(ref h, n.Note); Mix(ref h, n.Velocity); Mix(ref h, n.Tie); }
                Mix(ref h, st.Locks.Count);
                foreach (var kv in st.Locks) { Mix(ref h, (int)kv.Key); Mix(ref h, kv.Value); }
            }
        return h;
    }
}
