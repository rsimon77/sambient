using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sambient.App.PianoRoll;
using Sambient.App.ViewModels;
using Sambient.Seq;

namespace Sambient.App.Controls;

/// <summary>
/// Custom-drawn piano roll (pitch x time grid). All drawing happens in <see cref="Render"/>; all behaviour lives in
/// <see cref="PianoRollEditor"/> - this control forwards the pointer / wheel input and paints the editor's state:
/// key strip (left), step ruler with p-lock dots, probability and the draggable LENGTH marker (top), the note grid and the velocity lane.
/// </summary>
public sealed class PianoRollControl : Avalonia.Controls.Control
{
    public static readonly StyledProperty<PianoRollEditor?> EditorProperty = AvaloniaProperty.Register<PianoRollControl, PianoRollEditor?>(nameof(Editor));
    public PianoRollEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }

    // ---- palette (matches the rest of the app)
    private static readonly Typeface Face = new("Inter");
    private static IBrush B(string hex, double alpha = 1) => new SolidColorBrush(Color.Parse(hex), alpha);
    private static readonly IBrush Bg = B("#14171c"), RowWhite = B("#1a1e25"), RowBlack = B("#15181d"), ScaleTint = B("#5fb3ff", 0.075);
    private static readonly IBrush PastLength = B("#0b0d10", 0.62), RulerBg = B("#1b1f26"), VelBg = B("#12151a");
    private static readonly IBrush KeyWhite = B("#e8eaf0"), KeyBlack = B("#16181d"), KeyLit = B("#5fb3ff"), KeyHover = B("#ffd9a0");
    private static readonly IBrush Amber = B("#ffb454"), Green = B("#7be495"), Text = B("#c4c9d6"), TextDim = B("#6b7180");
    private static readonly IBrush Hover = B("#ffffff", 0.06), MarqueeFill = B("#5fb3ff", 0.14), ErasingFill = B("#d32f2f", 0.55);
    private static readonly Pen LineStep = new(B("#232831"), 1), LineBeat = new(B("#313846"), 1), LineBar = new(B("#46505f"), 1);
    private static readonly Pen RowLine = new(B("#1f242c"), 1), OctaveLine = new(B("#38404d"), 1);
    private static readonly Pen LengthPen = new(B("#ffb454"), 1.5), PlayPen = new(B("#7be495"), 1.5), MarqueePen = new(B("#5fb3ff"), 1);
    private static readonly Pen KeyEdge = new(B("#0e1013"), 1), SepPen = new(B("#2b303a"), 1);

    private static readonly Cursor CurArrow = new(StandardCursorType.Arrow), CurCross = new(StandardCursorType.Cross),
        CurHand = new(StandardCursorType.Hand), CurResize = new(StandardCursorType.SizeWestEast), CurPan = new(StandardCursorType.SizeAll);

    private readonly Dictionary<(string, int, int), FormattedText> _textCache = new();
    private PianoRollEditor? _subscribed;

    public PianoRollControl()
    {
        Focusable = true;
        ClipToBounds = true;
        FocusAdorner = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        if (_subscribed != null) _subscribed.Changed -= OnEditorChanged;
        _subscribed = change.NewValue as PianoRollEditor;
        if (_subscribed != null)
        {
            _subscribed.Changed += OnEditorChanged;
            if (Bounds.Width > 1) _subscribed.SetSize(Bounds.Width, Bounds.Height);
        }
        InvalidateVisual();
    }

    private void OnEditorChanged()
    {
        if (_subscribed != null) Cursor = CursorFor(_subscribed.Cursor);
        InvalidateVisual();
    }

    private static Cursor CursorFor(RollCursor c) => c switch
    {
        RollCursor.Cross => CurCross, RollCursor.Hand => CurHand, RollCursor.Resize => CurResize, RollCursor.Pan => CurPan, _ => CurArrow,
    };

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 380 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Editor?.SetSize(finalSize.Width, finalSize.Height);
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>Keyboard entry point for the window's key handler (only when this control has focus).</summary>
    public bool HandleKeyDown(KeyEventArgs e) => Editor?.KeyDown(e.Key, e.KeyModifiers) ?? false;

    // ------------------------------------------------------------------ input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var ed = Editor;
        if (ed == null) return;
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        RollButton? btn = props.IsLeftButtonPressed ? RollButton.Left : props.IsRightButtonPressed ? RollButton.Right
            : props.IsMiddleButtonPressed ? RollButton.Middle : null;
        if (btn == null) return;
        e.Pointer.Capture(this);
        ed.PointerPressed(e.GetPosition(this), e.KeyModifiers, btn.Value, e.ClickCount);
        if (!ed.IsDragging) e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Editor?.PointerMoved(e.GetPosition(this), e.KeyModifiers);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var ed = Editor;
        if (ed == null) return;
        ed.PointerReleased(e.GetPosition(this), e.KeyModifiers);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Editor?.PointerCancelled();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Editor?.PointerLeft();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Editor?.Wheel(e.GetPosition(this), e.Delta, e.KeyModifiers);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drawing

    private FormattedText Txt(string s, double size, IBrush brush, int brushId)
    {
        var key = (s, (int)(size * 10), brushId);
        if (_textCache.TryGetValue(key, out var ft)) return ft;
        if (_textCache.Count > 600) _textCache.Clear();
        ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);
        _textCache[key] = ft;
        return ft;
    }

    private static bool IsBlack(int note) => (note % 12) is 1 or 3 or 6 or 8 or 10;

    private static Color Lerp(Color a, Color b, double t)
        => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static readonly Color NoteLo = Color.Parse("#2a5d8e"), NoteHi = Color.Parse("#7fc7ff");
    private static readonly Color SelLo = Color.Parse("#a8742b"), SelHi = Color.Parse("#ffd48a");

    private static IBrush NoteBrush(int velocity, bool selected, double alpha)
    {
        double t = Math.Clamp((velocity - 1) / 126.0, 0, 1);
        return new SolidColorBrush(selected ? Lerp(SelLo, SelHi, t) : Lerp(NoteLo, NoteHi, t), alpha);
    }

    public override void Render(DrawingContext ctx)
    {
        var ed = Editor;
        var full = new Rect(Bounds.Size);
        ctx.DrawRectangle(Bg, null, full);
        if (ed == null || ed.Snapshot == null) return;

        var vp = ed.View;
        var grid = ed.GridRect;
        int length = ed.Length;
        int spb = RollGrid.StepsPerBeat(ed.Snapshot.StepNote);
        double lengthX = vp.StepToX(ed.LengthPreview ?? length);

        // ----- grid
        using (ctx.PushClip(grid))
        using (ctx.PushTransform(Matrix.CreateTranslation(grid.X, grid.Y)))
        {
            DrawRows(ctx, ed, vp, grid);
            DrawStepLines(ctx, vp, grid, spb);
            if (lengthX < grid.Width) ctx.DrawRectangle(PastLength, null, new Rect(Math.Max(0, lengthX), 0, grid.Width - Math.Max(0, lengthX), grid.Height));
            if (ed.HoverCell is { } hc && !ed.IsDragging && vp.NoteToY(hc.Note) is var hy && !double.IsNaN(hy) && hc.Step < length)
                ctx.DrawRectangle(Hover, null, new Rect(vp.StepToX(hc.Step), hy, vp.StepW, vp.RowH));
            DrawNotes(ctx, ed, vp, length);
            if (ed.MarqueeRect is { } m) ctx.DrawRectangle(MarqueeFill, MarqueePen, m);
            if (lengthX >= 0 && lengthX <= grid.Width) ctx.DrawLine(LengthPen, new Point(lengthX, 0), new Point(lengthX, grid.Height));
            DrawPlayhead(ctx, ed, vp, grid.Height);
            DrawScrollIndicators(ctx, vp, grid);
        }

        // ----- ruler
        var ruler = ed.RulerRect;
        using (ctx.PushClip(new Rect(0, 0, Bounds.Width, ruler.Height)))
        {
            ctx.DrawRectangle(RulerBg, null, new Rect(0, 0, Bounds.Width, ruler.Height));
            using (ctx.PushClip(ruler))
            using (ctx.PushTransform(Matrix.CreateTranslation(ruler.X, 0)))
                DrawRuler(ctx, ed, vp, ruler, spb, lengthX);
        }

        // ----- keys
        var keys = ed.KeysRect;
        using (ctx.PushClip(keys))
        using (ctx.PushTransform(Matrix.CreateTranslation(0, keys.Y)))
            DrawKeys(ctx, ed, vp, keys);

        // ----- velocity lane
        var vel = ed.VelRect;
        using (ctx.PushClip(new Rect(0, vel.Y, Bounds.Width, vel.Height)))
        using (ctx.PushTransform(Matrix.CreateTranslation(0, vel.Y)))
            DrawVelocity(ctx, ed, vp, vel, length, lengthX);

        ctx.DrawLine(SepPen, new Point(0, grid.Bottom + 0.5), new Point(Bounds.Width, grid.Bottom + 0.5));
    }

    private void DrawRows(DrawingContext ctx, PianoRollEditor ed, RollViewport vp, Rect grid)
    {
        for (int r = vp.FirstVisibleRow; r <= vp.LastVisibleRow; r++)
        {
            int note = vp.Rows.NoteOfRow(r);
            double y = vp.RowToY(r);
            var rect = new Rect(0, y, grid.Width, vp.RowH);
            ctx.DrawRectangle(IsBlack(note) ? RowBlack : RowWhite, null, rect);
            if (ed.ScaleVisible && ed.Scale is { } sc && sc[note % 12]) ctx.DrawRectangle(ScaleTint, null, rect);
            ctx.DrawLine(note % 12 == 0 ? OctaveLine : RowLine, new Point(0, y + vp.RowH), new Point(grid.Width, y + vp.RowH));
        }
    }

    private static void DrawStepLines(DrawingContext ctx, RollViewport vp, Rect grid, int spb)
    {
        for (int s = vp.FirstVisibleStep; s <= Math.Min(RollViewport.Steps, vp.LastVisibleStep + 1); s++)
        {
            int strength = RollGrid.LineStrength(s, spb);
            if (strength == 0 && vp.StepW < 7) continue;
            double x = Math.Round(vp.StepToX(s)) + 0.5;
            ctx.DrawLine(strength == 2 ? LineBar : strength == 1 ? LineBeat : LineStep, new Point(x, 0), new Point(x, grid.Height));
        }
    }

    private void DrawNotes(DrawingContext ctx, PianoRollEditor ed, RollViewport vp, int length)
    {
        foreach (var d in ed.DisplayNotes())
        {
            if (vp.NoteRect(d.Note, length) is not { } r) continue;
            if (r.Right < 0 || r.X > vp.GridW || r.Bottom < 0 || r.Y > vp.GridH) continue;
            bool inside = d.Note.Step < length;
            double alpha = !inside ? 0.3 : d.Ghost ? 0.62 : 1;
            var bar = new Rect(r.X + 0.5, r.Y + 1, Math.Max(2, r.Width - 1.5), Math.Max(3, r.Height - 2));
            var fill = d.Erasing ? ErasingFill : NoteBrush(ed.ShownVelocity(d.Note), d.Selected, alpha);
            var edge = new Pen(d.Selected ? B("#fff0cf", alpha) : B("#0b1119", 0.85 * alpha), 1);
            double rad = Math.Min(3, bar.Height / 2);
            ctx.DrawRectangle(fill, edge, bar, rad, rad);
            if (bar.Width > 16 && bar.Height >= 9)
            {
                var grip = B("#0b1119", 0.35 * alpha);
                ctx.DrawRectangle(grip, null, new Rect(bar.Right - 4, bar.Y + 3, 1.5, bar.Height - 6));
            }
            if (bar.Height >= 11 && bar.Width > 26)
                ctx.DrawText(Txt(NoteNames.Name(d.Note.Note), Math.Min(10, bar.Height - 3), B("#08101a", 0.9 * alpha), 1), new Point(bar.X + 4, bar.Y + (bar.Height - Math.Min(10, bar.Height - 3) * 1.2) / 2));
        }
    }

    private static void DrawPlayhead(DrawingContext ctx, PianoRollEditor ed, RollViewport vp, double height)
    {
        if (ed.Playhead < 0) return;
        double x = vp.StepToX(ed.Playhead);
        if (x >= 0 && x <= vp.GridW) ctx.DrawLine(PlayPen, new Point(x, 0), new Point(x, height));
    }

    private static void DrawScrollIndicators(DrawingContext ctx, RollViewport vp, Rect grid)
    {
        var br = B("#8a93a5", 0.32);
        if (vp.ContentH > vp.GridH + 1)
        {
            double th = Math.Max(16, grid.Height * vp.GridH / vp.ContentH);
            double ty = (grid.Height - th) * (vp.ScrollY / Math.Max(1, vp.ContentH - vp.GridH));
            ctx.DrawRectangle(br, null, new Rect(grid.Width - 5, ty, 3, th), 1.5, 1.5);
        }
        if (vp.ContentW > vp.GridW + 1)
        {
            double tw = Math.Max(16, grid.Width * vp.GridW / vp.ContentW);
            double tx = (grid.Width - tw) * (vp.ScrollX / Math.Max(1, vp.ContentW - vp.GridW));
            ctx.DrawRectangle(br, null, new Rect(tx, grid.Height - 5, tw, 3), 1.5, 1.5);
        }
    }

    private void DrawRuler(DrawingContext ctx, PianoRollEditor ed, RollViewport vp, Rect ruler, int spb, double lengthX)
    {
        var snap = ed.Snapshot!;
        int length = ed.Length;
        int labelEvery = vp.StepW >= 22 ? 1 : vp.StepW >= 12 ? 2 : spb;
        for (int s = vp.FirstVisibleStep; s <= vp.LastVisibleStep; s++)
        {
            double x = vp.StepToX(s);
            int strength = RollGrid.LineStrength(s, spb);
            ctx.DrawLine(strength == 2 ? LineBar : LineBeat, new Point(Math.Round(x) + 0.5, strength == 0 ? 17 : 10), new Point(Math.Round(x) + 0.5, ruler.Height));
            bool inside = s < length;
            if (s == ed.SelectedStep && inside)
                ctx.DrawRectangle(B("#ffb454", 0.22), null, new Rect(x + 1, 0, vp.StepW - 1, ruler.Height));
            if (s % labelEvery == 0 || s == ed.SelectedStep)
            {
                var ft = Txt((s + 1).ToString(CultureInfo.InvariantCulture), 10, s == ed.SelectedStep && inside ? Amber : inside ? Text : TextDim, s == ed.SelectedStep ? 2 : inside ? 3 : 4);
                ctx.DrawText(ft, new Point(x + 3, 1));
            }
            if (s < snap.Steps.Count && inside)
            {
                var st = snap.Steps[s];
                if (st.HasLocks) ctx.DrawEllipse(Amber, null, new Point(x + vp.StepW - 6, 7), 2.6, 2.6);
                if (st.Probability < 100 && vp.StepW >= 20)
                    ctx.DrawText(Txt(st.Probability + "%", 8.5, B("#ffd9a0"), 5), new Point(x + 3, 12));
                else if (st.Probability < 100)
                    ctx.DrawEllipse(B("#ffd9a0"), null, new Point(x + vp.StepW / 2, 18), 2, 2);
            }
        }
        // length end marker: a flag
        if (lengthX >= -8 && lengthX <= vp.GridW + 8)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(lengthX - 7, 0), true);
                g.LineTo(new Point(lengthX, 0)); g.LineTo(new Point(lengthX, ruler.Height)); g.LineTo(new Point(lengthX - 7, 8));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Amber, null, geo);
            ctx.DrawText(Txt("END", 7.5, B("#2a1b05"), 6), new Point(lengthX - 6.5, 0.5));
        }
        if (ed.Playhead >= 0)
        {
            double px = vp.StepToX(ed.Playhead);
            if (px >= 0 && px <= vp.GridW) ctx.DrawLine(PlayPen, new Point(px, 0), new Point(px, ruler.Height));
        }
    }

    private void DrawKeys(DrawingContext ctx, PianoRollEditor ed, RollViewport vp, Rect keys)
    {
        ctx.DrawRectangle(Bg, null, new Rect(0, 0, keys.Width, keys.Height));
        for (int r = vp.FirstVisibleRow; r <= vp.LastVisibleRow; r++)
        {
            int note = vp.Rows.NoteOfRow(r);
            double y = vp.RowToY(r);
            bool black = IsBlack(note);
            bool lit = ed.AuditionNoteShown == note;
            bool hov = ed.HoverCell is { } hc && hc.Note == note;
            var row = new Rect(0, y, keys.Width, vp.RowH);
            ctx.DrawRectangle(lit ? KeyLit : hov ? KeyHover : KeyWhite, KeyEdge, row);
            if (black) ctx.DrawRectangle(lit ? B("#3a86d6") : KeyBlack, null, new Rect(0, y + 0.5, keys.Width * 0.62, Math.Max(1, vp.RowH - 1)));
            if (note % 12 == 0 || (vp.RowH >= 16 && !black))
            {
                string label = note % 12 == 0 ? "C" + (note / 12 - 1) : NoteNames.Name(note);
                double fs = Math.Min(10.5, vp.RowH - 2);
                var ft = Txt(label, fs, note % 12 == 0 ? B("#3a4150") : TextDim, note % 12 == 0 ? 7 : 8);
                if (vp.RowH >= 9) ctx.DrawText(ft, new Point(keys.Width - ft.Width - 4, y + (vp.RowH - ft.Height) / 2));
            }
        }
        ctx.DrawLine(KeyEdge, new Point(keys.Width - 0.5, 0), new Point(keys.Width - 0.5, keys.Height));
    }

    private void DrawVelocity(DrawingContext ctx, PianoRollEditor ed, RollViewport vp, Rect vel, int length, double lengthX)
    {
        ctx.DrawRectangle(VelBg, null, new Rect(0, 0, Bounds.Width, vel.Height));
        ctx.DrawText(Txt("VELOCITY", 8.5, TextDim, 9), new Point(6, 6));
        ctx.DrawText(Txt("click · drag to paint", 8, TextDim, 10), new Point(6, 18));
        using (ctx.PushClip(new Rect(vel.X, 0, vel.Width, vel.Height)))
        using (ctx.PushTransform(Matrix.CreateTranslation(vel.X, 0)))
        {
            double usable = RollVelocity.UsableHeight(vel.Height);
            foreach (double f in new[] { 0.25, 0.5, 0.75, 1.0 })
            {
                double y = RollVelocity.PadTop + usable * (1 - f);
                ctx.DrawLine(RowLine, new Point(0, y), new Point(vp.GridW, y));
            }
            int spb = RollGrid.StepsPerBeat(ed.Snapshot!.StepNote);
            for (int s = vp.FirstVisibleStep; s <= Math.Min(RollViewport.Steps, vp.LastVisibleStep + 1); s++)
            {
                int strength = RollGrid.LineStrength(s, spb);
                if (strength == 0) continue;
                double x = Math.Round(vp.StepToX(s)) + 0.5;
                ctx.DrawLine(strength == 2 ? LineBar : LineStep, new Point(x, 0), new Point(x, vel.Height));
            }
            var notes = ed.Notes;
            for (int s = vp.FirstVisibleStep; s <= vp.LastVisibleStep; s++)
            {
                if (s >= ed.Length) break;
                var col = RollVelocity.NotesOfStep(notes, s);
                for (int i = 0; i < col.Count; i++)
                {
                    var n = col[i];
                    int v = ed.ShownVelocity(n);
                    bool sel = ed.Selection.Contains(n.Key);
                    var r = RollVelocity.BarRect(vp, s, i, col.Count, v, vel.Height);
                    ctx.DrawRectangle(NoteBrush(v, sel, 0.92), null, r, 1.5, 1.5);
                    ctx.DrawRectangle(B(sel ? "#fff0cf" : "#c9e8ff", 0.9), null, new Rect(r.X, r.Y, r.Width, 1.5));
                    if (sel && col.Count == 1 && vp.StepW >= 24)
                        ctx.DrawText(Txt(v.ToString(CultureInfo.InvariantCulture), 8.5, Text, 11), new Point(r.X + 1, Math.Max(0, r.Y - 10)));
                }
            }
            if (lengthX < vp.GridW) ctx.DrawRectangle(PastLength, null, new Rect(Math.Max(0, lengthX), 0, vp.GridW - Math.Max(0, lengthX), vel.Height));
            if (lengthX >= 0 && lengthX <= vp.GridW) ctx.DrawLine(LengthPen, new Point(lengthX, 0), new Point(lengthX, vel.Height));
            if (ed.Playhead >= 0)
            {
                double px = vp.StepToX(ed.Playhead);
                if (px >= 0 && px <= vp.GridW) ctx.DrawLine(PlayPen, new Point(px, 0), new Point(px, vel.Height));
            }
        }
    }
}
