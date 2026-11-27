using Avalonia;

namespace Sambient.App.PianoRoll;

public enum HitZone { None, Body, ResizeEdge }

public readonly record struct NoteHit(RollNote Note, HitZone Zone);

/// <summary>Finding the note under the pointer: body (move) or right edge (resize). Notes past the track length are not interactive.</summary>
public static class RollHitTest
{
    public const double MaxEdgePx = 8, MinEdgePx = 3;

    /// <summary>Width of the resize zone at the right end of a bar: 8 px, a third of the bar for narrow ones, at least 3 px.</summary>
    public static double EdgeWidth(double barWidth) => Math.Clamp(barWidth / 3, MinEdgePx, MaxEdgePx);

    /// <summary>The topmost (last listed) note under <paramref name="p"/> (grid coordinates), or null.</summary>
    public static NoteHit? Hit(IReadOnlyList<RollNote> notes, RollViewport vp, int length, Point p)
    {
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            var n = notes[i];
            if (n.Step >= length) continue;
            if (vp.NoteRect(n, length) is not { } r || !r.Contains(p)) continue;
            return new NoteHit(n, p.X >= r.Right - EdgeWidth(r.Width) ? HitZone.ResizeEdge : HitZone.Body);
        }
        return null;
    }
}
