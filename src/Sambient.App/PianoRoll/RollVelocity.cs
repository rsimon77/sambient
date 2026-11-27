using Avalonia;

namespace Sambient.App.PianoRoll;

/// <summary>Geometry of the velocity lane: one bar per note at its start step; several notes of a step sit side by side (low to high pitch).</summary>
public static class RollVelocity
{
    public const double PadTop = 4, PadBottom = 3;

    public static double UsableHeight(double laneHeight) => Math.Max(1, laneHeight - PadTop - PadBottom);

    /// <summary>The notes of a step in lane order.</summary>
    public static List<RollNote> NotesOfStep(IEnumerable<RollNote> notes, int step)
        => notes.Where(n => n.Step == step).OrderBy(n => n.Note).ToList();

    /// <summary>Bar rectangle in lane coordinates (x like the grid, y 0 = top of the lane).</summary>
    public static Rect BarRect(RollViewport vp, int step, int index, int count, int velocity, double laneHeight)
    {
        double colX = vp.StepToX(step) + 1, colW = Math.Max(2, vp.StepW - 2);
        double w = Math.Max(2, colW / Math.Max(1, count));
        double usable = UsableHeight(laneHeight);
        double h = Math.Max(2, usable * velocity / 127.0);
        return new Rect(colX + index * w, PadTop + usable - h, Math.Max(1.5, w - (count > 1 ? 1 : 0)), h);
    }

    /// <summary>The bar under an x position (any y), or null when the column has no note / lies past the track length.</summary>
    public static RollNote? HitBar(IReadOnlyList<RollNote> notes, RollViewport vp, int length, double x)
    {
        double pos = vp.XToStep(x);
        int step = (int)Math.Floor(pos);
        if (step < 0 || step >= length || step >= RollViewport.Steps) return null;
        var col = NotesOfStep(notes, step);
        if (col.Count == 0) return null;
        int idx = Math.Min(col.Count - 1, (int)((pos - step) * col.Count));
        return col[Math.Max(0, idx)];
    }
}
