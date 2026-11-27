using Avalonia;
using Sambient.Seq;

namespace Sambient.App.PianoRoll;

/// <summary>
/// The mapping between pixels of the grid area (origin = its top-left corner) and (step, row / pitch), including scroll and zoom.
/// Columns are the 64 steps (<see cref="StepW"/> px each), rows come from a <see cref="RowMap"/> (<see cref="RowH"/> px each).
/// Pure geometry: no drawing, no input.
/// </summary>
public sealed class RollViewport
{
    public const double MinStepW = 4, MaxStepW = 120, MinRowH = 6, MaxRowH = 40;
    public const int Steps = SeqLimits.MaxSteps;

    public RowMap Rows { get; set; } = RowMap.Full;
    public double StepW { get; private set; } = 28;
    public double RowH { get; private set; } = 14;
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
    public double GridW { get; private set; } = 600;
    public double GridH { get; private set; } = 300;

    public double ContentW => Steps * StepW;
    public double ContentH => Rows.Count * RowH;

    public void SetSize(double w, double h) { GridW = Math.Max(1, w); GridH = Math.Max(1, h); Clamp(); }
    public void SetScale(double stepW, double rowH) { StepW = Math.Clamp(stepW, MinStepW, MaxStepW); RowH = Math.Clamp(rowH, MinRowH, MaxRowH); Clamp(); }

    public void Clamp()
    {
        ScrollX = Math.Clamp(ScrollX, 0, Math.Max(0, ContentW - GridW));
        ScrollY = Math.Clamp(ScrollY, 0, Math.Max(0, ContentH - GridH));
    }

    // ---- x / step
    public double StepToX(double step) => step * StepW - ScrollX;
    public double XToStep(double x) => (x + ScrollX) / StepW;
    /// <summary>Index of the step column under x, clamped to 0..63.</summary>
    public int XToStepIndex(double x) => Math.Clamp((int)Math.Floor(XToStep(x)), 0, Steps - 1);

    // ---- y / row / pitch
    public double RowToY(int row) => row * RowH - ScrollY;
    public int YToRow(double y) => Math.Clamp((int)Math.Floor((y + ScrollY) / RowH), 0, Rows.Count - 1);
    public int YToNote(double y) => Rows.NoteOfRow(YToRow(y));
    /// <summary>Top of the row of a pitch (NaN when the pitch is not shown).</summary>
    public double NoteToY(int note) { int r = Rows.RowOfNote(note); return r < 0 ? double.NaN : RowToY(r); }

    /// <summary>The bar of a note (null when its pitch is not shown). Ties are cut at <paramref name="length"/> for notes inside the track.</summary>
    public Rect? NoteRect(RollNote n, int length)
    {
        int row = Rows.RowOfNote(n.Note);
        if (row < 0) return null;
        return new Rect(StepToX(n.Step), RowToY(row), n.VisibleTie(length) * StepW, RowH);
    }

    // ---- visible ranges
    public int FirstVisibleStep => Math.Clamp((int)Math.Floor(ScrollX / StepW), 0, Steps - 1);
    public int LastVisibleStep => Math.Clamp((int)Math.Ceiling((ScrollX + GridW) / StepW) - 1, 0, Steps - 1);
    public int FirstVisibleRow => Math.Clamp((int)Math.Floor(ScrollY / RowH), 0, Rows.Count - 1);
    public int LastVisibleRow => Math.Clamp((int)Math.Ceiling((ScrollY + GridH) / RowH) - 1, 0, Rows.Count - 1);

    // ---- zoom / scroll
    /// <summary>Horizontal zoom keeping the step under <paramref name="anchorX"/> where it is.</summary>
    public void ZoomX(double factor, double anchorX)
    {
        double stepAtAnchor = XToStep(anchorX);
        StepW = Math.Clamp(StepW * factor, MinStepW, MaxStepW);
        ScrollX = stepAtAnchor * StepW - anchorX;
        Clamp();
    }

    /// <summary>Vertical zoom keeping the row under <paramref name="anchorY"/> where it is.</summary>
    public void ZoomY(double factor, double anchorY)
    {
        double rowAtAnchor = (anchorY + ScrollY) / RowH;
        RowH = Math.Clamp(RowH * factor, MinRowH, MaxRowH);
        ScrollY = rowAtAnchor * RowH - anchorY;
        Clamp();
    }

    /// <summary>Scales so that <paramref name="length"/> steps fill the grid width, scrolled to the start.</summary>
    public void FitSteps(int length)
    {
        StepW = Math.Clamp(GridW / Math.Max(1, length), MinStepW, MaxStepW);
        ScrollX = 0;
        Clamp();
    }

    /// <summary>Scrolls so the pitch is vertically centred (clamped).</summary>
    public void CenterOnNote(int note)
    {
        int row = Rows.NearestRow(note);
        ScrollY = (row + 0.5) * RowH - GridH / 2;
        Clamp();
    }

    /// <summary>Scrolls horizontally so the step is visible; true when the view moved.</summary>
    public bool EnsureStepVisible(double step)
    {
        double x = StepToX(step);
        if (x >= 0 && x < GridW) return false;
        double pageSteps = Math.Max(1, Math.Floor(GridW / StepW));
        ScrollX = Math.Floor(step / pageSteps) * pageSteps * StepW;
        Clamp();
        return true;
    }
}
