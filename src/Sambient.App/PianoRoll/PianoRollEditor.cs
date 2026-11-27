using Avalonia;
using Avalonia.Input;
using Sambient.App.ViewModels;
using Sambient.Seq;

namespace Sambient.App.PianoRoll;

public enum RollTool { Draw, Select, Erase }
public enum RollZone { None, Keys, Ruler, Grid, Velocity }
public enum RollCursor { Arrow, Cross, Hand, Resize, Pan }
public enum RollButton { Left, Right, Middle }

/// <summary>A note as drawn: possibly a ghost of an ongoing gesture.</summary>
public readonly record struct DrawNote(RollNote Note, bool Selected, bool Ghost, bool Erasing);

/// <summary>
/// The piano-roll interaction logic, independent of drawing: layout, view (scroll / zoom / fold), tools, selection, the mouse
/// gesture state machine (add + drag length, move, resize, marquee, erase sweep, velocity painting, length marker), keyboard commands,
/// clipboard and undo. The Avalonia control only forwards input and paints what the editor exposes.
///
/// Gestures work on previews and commit ONE atomic batch of note edits on release (so the audio thread never sees half a drag and
/// every gesture is one undo step). Pointer positions are in the control's coordinates.
/// </summary>
public sealed class PianoRollEditor
{
    public const double KeyW = 54, RulerH = 24, VelH = 66, VelGap = 6, DragThreshold = 3, LengthGrabPx = 7;
    private const int StatusTicks = 100;

    private enum Gesture { None, Add, Move, Resize, Marquee, Erase, Ruler, Length, Velocity, Pan, Keys }

    private readonly IRollHost _host;
    private TrackSnapshot? _snap;
    private IReadOnlyList<RollNote> _notes = [];
    private bool _viewInit, _sized;
    private int _statusTicks;
    private RollOps.Clip? _clip;

    public PianoRollEditor(IRollHost host) { _host = host; }

    // ------------------------------------------------------------------ state

    public RollViewport View { get; } = new();
    public RollSelection Selection { get; } = new();
    public RollHistory History { get; } = new();
    public RollTool Tool { get; set; } = RollTool.Draw;
    public int Track { get; private set; } = -1;
    public int Length { get; private set; } = 16;
    public IReadOnlyList<RollNote> Notes => _notes;
    public TrackSnapshot? Snapshot => _snap;
    public int LastTie { get; set; } = 1;
    public int LastVelocity { get; set; } = 100;
    public double Width { get; private set; } = 700;
    public double Height { get; private set; } = 400;
    /// <summary>Step marked as selected in the step editor (set by the application).</summary>
    public int SelectedStep { get; set; }
    /// <summary>Playhead position in steps (step + phase), or -1.</summary>
    public double Playhead { get; private set; } = -1;
    public string StatusText { get; private set; } = "";
    public bool[]? Scale { get; set; }
    public bool ScaleVisible { get; set; }
    public (int Step, int Note)? HoverCell { get; private set; }
    public int AuditionNoteShown { get; private set; } = -1;

    public event Action? Changed;
    /// <summary>A note was double-clicked (its step has been selected in the step editor).</summary>
    public event Action<RollNote>? NoteActivated;

    private void Raise() => Changed?.Invoke();

    public Rect GridRect => new(KeyW, RulerH, Math.Max(1, Width - KeyW), Math.Max(1, Height - RulerH - VelGap - VelH));
    public Rect VelRect => new(KeyW, Height - VelH, Math.Max(1, Width - KeyW), VelH);
    public Rect RulerRect => new(KeyW, 0, Math.Max(1, Width - KeyW), RulerH);
    public Rect KeysRect => new(0, RulerH, KeyW, GridRect.Height);

    public RollZone ZoneAt(Point p)
    {
        if (GridRect.Contains(p)) return RollZone.Grid;
        if (RulerRect.Contains(p)) return RollZone.Ruler;
        if (KeysRect.Contains(p)) return RollZone.Keys;
        if (VelRect.Contains(p)) return RollZone.Velocity;
        return RollZone.None;
    }

    private Point ToGrid(Point p) => new(p.X - GridRect.X, p.Y - GridRect.Y);

    // ------------------------------------------------------------------ data in

    public void SetSize(double w, double h)
    {
        if (w < 1 || h < 1) return;
        Width = w; Height = h; _sized = true;
        View.SetSize(GridRect.Width, GridRect.Height);
        if (!_viewInit) ResetView();
        Raise();
    }

    /// <summary>The track changed (an edit, a refresh, another layer). Selection of vanished notes is dropped.</summary>
    public void SetTrack(TrackSnapshot t)
    {
        bool trackChanged = t.Track != Track;
        Track = t.Track; Length = t.Length; _snap = t;
        _notes = RollNote.FromTrack(t);
        if (trackChanged) { Selection.Clear(); CancelGesture(); } else Selection.Prune(_notes);
        RebuildRows();
        if (trackChanged) ResetView();
        Raise();
    }

    private bool _fold;
    public bool Fold
    {
        get => _fold;
        set
        {
            if (_fold == value) return;
            int centre = View.Rows.NoteOfRow(View.YToRow(View.GridH / 2));       // keep looking at the same pitch
            _fold = value;
            RebuildRows();
            View.CenterOnNote(centre);
            Raise();
        }
    }

    private void RebuildRows()
    {
        View.Rows = _fold ? RowMap.Folded(_notes.Select(n => n.Note)) : RowMap.Full;
        View.Clamp();
    }

    /// <summary>Fits the track length horizontally and centres the pitch range of the notes (C4 for an empty track) vertically.</summary>
    public void ResetView()
    {
        if (!_sized) { _viewInit = false; return; }
        _viewInit = true;
        View.SetSize(GridRect.Width, GridRect.Height);
        View.FitSteps(Length);
        int centre = _notes.Count == 0 ? 60 : (_notes.Min(n => n.Note) + _notes.Max(n => n.Note)) / 2;
        View.CenterOnNote(centre);
    }

    public void SetPlayhead(double pos, bool follow)
    {
        if (pos == Playhead) return;
        Playhead = pos;
        if (follow && pos >= 0 && _gesture == Gesture.None) View.EnsureStepVisible(pos);
        Raise();
    }

    public void SetStatus(string text) { StatusText = text; _statusTicks = text.Length == 0 ? 0 : StatusTicks; Raise(); }

    /// <summary>Called from the 30 Hz tick: lets status messages fade out.</summary>
    public void Tick()
    {
        if (_statusTicks > 0 && --_statusTicks == 0) { StatusText = ""; Raise(); }
    }

    public void RefreshSelection() => Raise();

    // ------------------------------------------------------------------ gesture state

    private Gesture _gesture = Gesture.None;
    private Point _downP, _lastP;           // grid coordinates
    private bool _moved;
    private bool _shift;
    private RollNote _grab;
    private int _downCol, _downNote;
    private List<RollNote> _dragSel = new();
    private int _dStep, _dNote, _resizeDelta, _addStep, _addNote, _addTie;
    private int _lengthPreview;
    private bool _toggleOffOnRelease, _collapseOnRelease;
    private List<NoteKey> _marqueeBase = new();
    private readonly HashSet<NoteKey> _erased = new();
    private readonly Dictionary<NoteKey, int> _velOverride = new();
    private int _velLast = -1;
    private int _auditioned = -1;

    public bool IsDragging => _gesture != Gesture.None;
    public Rect? MarqueeRect { get; private set; }
    public int? LengthPreview => _gesture == Gesture.Length ? _lengthPreview : null;
    public RollCursor Cursor { get; private set; } = RollCursor.Cross;

    /// <summary>Notes as they should be drawn now, previews of the running gesture included.</summary>
    public IEnumerable<DrawNote> DisplayNotes()
    {
        bool moving = _gesture == Gesture.Move && _moved;
        bool resizing = _gesture == Gesture.Resize && _resizeDelta != 0;
        HashSet<NoteKey>? hidden = moving || resizing ? _dragSel.Select(n => n.Key).ToHashSet() : null;
        foreach (var n in _notes)
        {
            if (hidden != null && hidden.Contains(n.Key)) continue;
            var shown = n;
            if (_velOverride.TryGetValue(n.Key, out int v)) shown = n with { Velocity = v };
            yield return new DrawNote(shown, Selection.Contains(n.Key), false, _erased.Contains(n.Key));
        }
        if (moving)
            foreach (var n in _dragSel)
                yield return new DrawNote(n with { Step = n.Step + _dStep, Note = n.Note + _dNote }, true, true, false);
        else if (resizing)
            foreach (var n in _dragSel)
                yield return new DrawNote(n with { Tie = Math.Clamp(n.Tie + _resizeDelta, 1, Math.Max(1, Length - n.Step)) }, true, true, false);
        if (_gesture == Gesture.Add)
            yield return new DrawNote(new RollNote(_addStep, _addNote, LastVelocity, _addTie), true, true, false);
    }

    /// <summary>The velocity a bar currently shows (overrides of a running paint included).</summary>
    public int ShownVelocity(RollNote n) => _velOverride.TryGetValue(n.Key, out int v) ? v : n.Velocity;

    private void CancelGesture()
    {
        EndAudition();
        _gesture = Gesture.None; _moved = false; MarqueeRect = null;
        _erased.Clear(); _velOverride.Clear(); _dragSel.Clear();
    }

    private void EndAudition()
    {
        if (_auditioned >= 0) { _host.Audition(_auditioned, false); _auditioned = -1; AuditionNoteShown = -1; }
    }

    private void StartAudition(int note)
    {
        if (_auditioned == note) return;
        EndAudition();
        _auditioned = note; AuditionNoteShown = note;
        _host.Audition(note, true);
    }

    // ------------------------------------------------------------------ pointer

    public void PointerPressed(Point p, KeyModifiers mods, RollButton button, int clickCount)
    {
        if (_gesture != Gesture.None) return;
        _shift = mods.HasFlag(KeyModifiers.Shift);
        _moved = false;
        var zone = ZoneAt(p);
        if (button == RollButton.Middle) { _gesture = Gesture.Pan; _downP = p; _lastP = p; Cursor = RollCursor.Pan; Raise(); return; }
        switch (zone)
        {
            case RollZone.Keys when button == RollButton.Left: PressKeys(p); break;
            case RollZone.Ruler when button == RollButton.Left: PressRuler(p); break;
            case RollZone.Grid: PressGrid(p, mods, button, clickCount); break;
            case RollZone.Velocity when button == RollButton.Left: PressVelocity(p); break;
        }
    }

    private void PressKeys(Point p)
    {
        _gesture = Gesture.Keys;
        StartAudition(View.YToNote(p.Y - KeysRect.Y));
        Raise();
    }

    private void PressRuler(Point p)
    {
        double markerX = GridRect.X + View.StepToX(Length);
        if (Math.Abs(p.X - markerX) <= LengthGrabPx)
        {
            _gesture = Gesture.Length; _lengthPreview = Length; Cursor = RollCursor.Resize;
        }
        else
        {
            _gesture = Gesture.Ruler;
            SelectStepAt(p.X - GridRect.X);
        }
        Raise();
    }

    private void SelectStepAt(double gx)
    {
        int s = View.XToStepIndex(gx);
        if (s < Length) { SelectedStep = s; _host.SelectStep(s); Raise(); }
    }

    private void PressGrid(Point p, KeyModifiers mods, RollButton button, int clickCount)
    {
        var g = ToGrid(p);
        _downP = g; _lastP = g;
        var hit = RollHitTest.Hit(_notes, View, Length, g);
        bool erase = button == RollButton.Right || (button == RollButton.Left && (Tool == RollTool.Erase || mods.HasFlag(KeyModifiers.Alt)));
        if (button == RollButton.Right && !erase) return;
        if (erase)
        {
            _gesture = Gesture.Erase; _erased.Clear();
            if (hit is { } h) _erased.Add(h.Note.Key);
            Raise();
            return;
        }
        if (clickCount >= 2 && hit is { } dbl)
        {
            _host.SelectStep(dbl.Note.Step);
            SelectedStep = dbl.Note.Step;
            NoteActivated?.Invoke(dbl.Note);
            return;
        }
        bool marqueeMod = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta);
        if (hit is { } hn)
        {
            var key = hn.Note.Key;
            _toggleOffOnRelease = false; _collapseOnRelease = false;
            if (_shift)
            {
                if (Selection.Contains(key)) _toggleOffOnRelease = true; else Selection.Add(key);
            }
            else if (!Selection.Contains(key)) Selection.Set([key]);
            else if (Selection.Count > 1) _collapseOnRelease = true;
            _grab = hn.Note;
            _dragSel = _notes.Where(n => Selection.Contains(n.Key)).ToList();
            if (_dragSel.Count == 0) _dragSel = [hn.Note];
            if (hn.Zone == HitZone.ResizeEdge)
            {
                _gesture = Gesture.Resize; _resizeDelta = 0; Cursor = RollCursor.Resize;
            }
            else
            {
                _gesture = Gesture.Move; _dStep = 0; _dNote = 0;
                _downCol = (int)Math.Floor(View.XToStep(g.X));
                _downNote = View.YToNote(g.Y);
            }
            Raise();
            return;
        }
        if (Tool == RollTool.Select || marqueeMod)
        {
            _gesture = Gesture.Marquee;
            _marqueeBase = _shift ? Selection.Keys.ToList() : new List<NoteKey>();
            if (!_shift) Selection.Clear();
            MarqueeRect = new Rect(g, g);
            Raise();
            return;
        }
        // Draw: add a note and drag its end
        int step = View.XToStepIndex(g.X);
        if (step >= Length) { SetStatus($"Step {step + 1} is outside the track length ({Length})"); return; }
        _gesture = Gesture.Add;
        _addStep = step; _addNote = View.YToNote(g.Y);
        _addTie = Math.Clamp(LastTie, 1, Math.Max(1, Length - step));
        if (!_shift) Selection.Clear();
        StartAudition(_addNote);
        Raise();
    }

    private void PressVelocity(Point p)
    {
        double x = p.X - VelRect.X;
        var bar = RollVelocity.HitBar(_notes, View, Length, x);
        if (bar is not { } b) return;
        _gesture = Gesture.Velocity;
        _lastP = new Point(x, p.Y - VelRect.Y);
        _velOverride.Clear();
        _velLast = VelocityAt(_lastP.Y);
        _velOverride[b.Key] = _velLast;
        Raise();
    }

    private int VelocityAt(double laneY) => RollOps.VelocityFromY(laneY, RollVelocity.PadTop, RollVelocity.UsableHeight(VelH));

    public void PointerMoved(Point p, KeyModifiers mods)
    {
        var zone = ZoneAt(p);
        switch (_gesture)
        {
            case Gesture.None: UpdateHover(p, zone, mods); return;
            case Gesture.Pan:
                View.ScrollX -= p.X - _lastP.X; View.ScrollY -= p.Y - _lastP.Y; View.Clamp(); _lastP = p; Raise(); return;
            case Gesture.Keys:
                StartAudition(View.YToNote(Math.Clamp(p.Y - KeysRect.Y, 0, KeysRect.Height - 1)));
                Raise(); return;
            case Gesture.Ruler:
                SelectStepAt(p.X - GridRect.X); return;
            case Gesture.Length:
                _lengthPreview = Math.Clamp((int)Math.Round(View.XToStep(p.X - GridRect.X), MidpointRounding.AwayFromZero), 1, SeqLimits.MaxSteps);
                Raise(); return;
            case Gesture.Velocity: MoveVelocity(p); return;
        }
        var g = ToGrid(p);
        if (!_moved && (g - _downP).X * (g - _downP).X + (g - _downP).Y * (g - _downP).Y >= DragThreshold * DragThreshold) _moved = true;
        switch (_gesture)
        {
            case Gesture.Add:
                if (_moved) _addTie = RollOps.TieFromPosition(_addStep, View.XToStep(g.X), Length);
                break;
            case Gesture.Move:
                if (!_moved) break;
                int dStep = (int)Math.Floor(View.XToStep(g.X)) - _downCol;
                int dNote = View.YToNote(g.Y) - _downNote;
                (_dStep, _dNote) = RollOps.ClampDelta(_dragSel, Length, dStep, dNote);
                break;
            case Gesture.Resize:
                if (!_moved) break;
                _resizeDelta = RollOps.TieFromPosition(_grab.Step, View.XToStep(g.X), Length) - _grab.Tie;
                break;
            case Gesture.Marquee:
                MarqueeRect = new Rect(Math.Min(_downP.X, g.X), Math.Min(_downP.Y, g.Y), Math.Abs(g.X - _downP.X), Math.Abs(g.Y - _downP.Y));
                Selection.Marquee(_notes, View, Length, MarqueeRect.Value, _marqueeBase);
                break;
            case Gesture.Erase:
                SweepErase(_lastP, g);
                break;
        }
        _lastP = g;
        Raise();
    }

    private void SweepErase(Point a, Point b)
    {
        double dist = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        int n = Math.Max(1, (int)Math.Ceiling(dist / 3));
        for (int i = 1; i <= n; i++)
        {
            var q = new Point(a.X + (b.X - a.X) * i / n, a.Y + (b.Y - a.Y) * i / n);
            if (RollHitTest.Hit(_notes, View, Length, q) is { } h) _erased.Add(h.Note.Key);
        }
    }

    private void MoveVelocity(Point p)
    {
        var to = new Point(p.X - VelRect.X, p.Y - VelRect.Y);
        var from = _lastP;
        int c0 = (int)Math.Floor(View.XToStep(from.X)), c1 = (int)Math.Floor(View.XToStep(to.X));
        int lo = Math.Min(c0, c1), hi = Math.Max(c0, c1);
        for (int c = lo; c <= hi; c++)
        {
            if (c < 0 || c >= Length) continue;
            if (c == c1)
            {
                if (RollVelocity.HitBar(_notes, View, Length, to.X) is { } bar) _velOverride[bar.Key] = VelocityAt(to.Y);
            }
            else if (c0 != c1 && c != c0)
            {
                double cx = View.StepToX(c + 0.5);
                double t = Math.Abs(to.X - from.X) < 0.001 ? 1 : Math.Clamp((cx - from.X) / (to.X - from.X), 0, 1);
                int v = VelocityAt(from.Y + t * (to.Y - from.Y));
                foreach (var n in RollVelocity.NotesOfStep(_notes, c)) _velOverride[n.Key] = v;
            }
        }
        _velLast = VelocityAt(to.Y);
        _lastP = to;
        Raise();
    }

    private void UpdateHover(Point p, RollZone zone, KeyModifiers mods)
    {
        var cursor = RollCursor.Arrow;
        (int, int)? cell = null;
        if (zone == RollZone.Grid)
        {
            var g = ToGrid(p);
            if (RollHitTest.Hit(_notes, View, Length, g) is { } h) cursor = h.Zone == HitZone.ResizeEdge ? RollCursor.Resize : RollCursor.Hand;
            else
            {
                cursor = Tool == RollTool.Select ? RollCursor.Arrow : RollCursor.Cross;
                cell = (View.XToStepIndex(g.X), View.YToNote(g.Y));
            }
        }
        else if (zone == RollZone.Ruler && Math.Abs(p.X - (GridRect.X + View.StepToX(Length))) <= LengthGrabPx) cursor = RollCursor.Resize;
        else if (zone == RollZone.Keys) cursor = RollCursor.Hand;
        if (cursor != Cursor || cell != HoverCell) { Cursor = cursor; HoverCell = cell; Raise(); }
        _hoverStep = zone == RollZone.Grid ? View.XToStepIndex(p.X - GridRect.X) : -1;
    }

    private int _hoverStep = -1;

    public void PointerLeft()
    {
        if (_gesture != Gesture.None) return;
        _hoverStep = -1;
        if (HoverCell != null) { HoverCell = null; Raise(); }
    }

    public void PointerReleased(Point p, KeyModifiers mods)
    {
        var g = _gesture;
        if (g == Gesture.None) return;
        _gesture = Gesture.None;
        MarqueeRect = null;
        Cursor = RollCursor.Cross;
        switch (g)
        {
            case Gesture.Keys: EndAudition(); break;
            case Gesture.Length:
                if (_lengthPreview != Length) CommitLength(_lengthPreview);
                break;
            case Gesture.Add: ReleaseAdd(); break;
            case Gesture.Move: ReleaseMove(); break;
            case Gesture.Resize: ReleaseResize(); break;
            case Gesture.Erase: ReleaseErase(); break;
            case Gesture.Velocity: ReleaseVelocity(); break;
            case Gesture.Marquee: break;
        }
        _dragSel.Clear();
        Raise();
    }

    /// <summary>The capture was lost or Escape was pressed: nothing is committed.</summary>
    public void PointerCancelled()
    {
        if (_gesture == Gesture.None) return;
        CancelGesture();
        Raise();
    }

    // ------------------------------------------------------------------ commits

    private NoteEditResult? Commit(IReadOnlyList<NoteEdit> edits)
    {
        if (edits.Count == 0) return null;
        var before = _host.Capture();
        var res = _host.Apply(edits);
        if (res.Applied > 0)
        {
            History.Push(_host.Track, before);
            _host.Committed();
            Reload();
        }
        if (res.Failed > 0) ReportFailures(edits, res);
        return res;
    }

    private void ReportFailures(IReadOnlyList<NoteEdit> edits, NoteEditResult res)
    {
        for (int i = 0; i < edits.Count; i++)
        {
            if (res.Succeeded[i]) continue;
            var e = edits[i];
            int step = e.Kind == NoteEditKind.Move ? e.ToStep : e.Step;
            bool full = _notes.Count(n => n.Step == step) >= SeqLimits.MaxNotesPerStep;
            string msg = full ? $"Step {step + 1} already holds {SeqLimits.MaxNotesPerStep} notes"
                : e.Kind == NoteEditKind.Move ? $"{NoteNames.Name(e.ToNote)} already sounds at step {step + 1}" : "Edit not possible";
            if (res.Failed > 1) msg += $" ({res.Failed} notes blocked)";
            SetStatus(msg);
            return;
        }
    }

    private void Reload()
    {
        SetTrack(_host.GetTrack());
    }

    private void CommitLength(int length)
    {
        var before = _host.Capture();
        _host.SetLength(length);
        History.Push(_host.Track, before);
        _host.Committed();
        Reload();
    }

    private void ReleaseAdd()
    {
        EndAudition();
        var res = Commit([NoteEdit.Add(_addStep, _addNote, LastVelocity, _addTie)]);
        if (res is { Applied: > 0 })
        {
            if (_moved) LastTie = _addTie;
            Selection.Set([new NoteKey(_addStep, _addNote)]);
            _host.SelectStep(_addStep);
            SelectedStep = _addStep;
        }
    }

    private void ReleaseMove()
    {
        if (!_moved || (_dStep == 0 && _dNote == 0))
        {
            // a click: collapse / toggle the selection
            var k = _grab.Key;
            if (_toggleOffOnRelease) Selection.Toggle(k);
            else if (_collapseOnRelease) Selection.Set([k]);
            return;
        }
        var edits = RollOps.MoveEdits(_dragSel, _dStep, _dNote);
        var res = Commit(edits);
        if (res != null) Selection.Set(RollOps.SelectionAfterMove(edits, res));
        if (_dNote != 0) _host.AuditionTap(Math.Clamp(_grab.Note + _dNote, 0, 127));
    }

    private void ReleaseResize()
    {
        if (!_moved || _resizeDelta == 0)
        {
            if (_toggleOffOnRelease) Selection.Toggle(_grab.Key);
            return;
        }
        Commit(RollOps.ResizeEdits(_dragSel, _resizeDelta));
        LastTie = Math.Clamp(_grab.Tie + _resizeDelta, 1, SeqLimits.MaxSteps);
    }

    private void ReleaseErase()
    {
        if (_erased.Count == 0) return;
        var keys = _erased.ToList();
        _erased.Clear();
        Commit(RollOps.RemoveEdits(keys));
        foreach (var k in keys) Selection.Set(Selection.Keys.Where(x => x != k).ToList());
    }

    private void ReleaseVelocity()
    {
        if (_velOverride.Count == 0) return;
        var edits = _velOverride.Select(kv => NoteEdit.SetVelocity(kv.Key.Step, kv.Key.Note, kv.Value)).ToList();
        _velOverride.Clear();
        Commit(edits);
        if (_velLast > 0) LastVelocity = _velLast;
    }

    // ------------------------------------------------------------------ wheel

    /// <summary>Wheel / trackpad: Cmd/Ctrl = horizontal zoom, Alt = vertical zoom, Shift (or horizontal delta) = scroll sideways, otherwise scroll up / down.</summary>
    public void Wheel(Point p, Vector delta, KeyModifiers mods)
    {
        var g = ToGrid(p);
        bool cmd = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta);
        double dy = delta.Y, dx = delta.X;
        if (cmd) View.ZoomX(Math.Pow(1.15, dy), Math.Clamp(g.X, 0, View.GridW));
        else if (mods.HasFlag(KeyModifiers.Alt))
        {
            // macOS turns Alt + vertical scroll into horizontal deltas
            double d = Math.Abs(dy) >= Math.Abs(dx) ? dy : dx;
            View.ZoomY(Math.Pow(1.12, d), Math.Clamp(g.Y, 0, View.GridH));
        }
        else if (mods.HasFlag(KeyModifiers.Shift)) { View.ScrollX -= (Math.Abs(dx) > Math.Abs(dy) ? dx : dy) * 60; View.Clamp(); }
        else { View.ScrollX -= dx * 60; View.ScrollY -= dy * View.RowH * 3; View.Clamp(); }
        Raise();
    }

    // ------------------------------------------------------------------ keyboard

    private static bool IsCmd(KeyModifiers m) => m.HasFlag(KeyModifiers.Meta) || m.HasFlag(KeyModifiers.Control);

    /// <summary>Handles a key press while the piano roll has focus; returns true when it was used. Does nothing for keys it does not own, so global shortcuts stay.</summary>
    public bool KeyDown(Key key, KeyModifiers mods)
    {
        bool cmd = IsCmd(mods), shift = mods.HasFlag(KeyModifiers.Shift), alt = mods.HasFlag(KeyModifiers.Alt);
        if (alt) return false;
        if (cmd)
        {
            switch (key)
            {
                case Key.A: Selection.SelectAll(_notes, Length); Raise(); return true;
                case Key.C: CopySelection(); return true;
                case Key.X: CopySelection(); DeleteSelection(); return true;
                case Key.V: Paste(); return true;
                case Key.Z: if (shift) Redo(); else Undo(); return true;
                case Key.Y: Redo(); return true;
            }
            return false;
        }
        if (key == Key.Escape)
        {
            if (_gesture != Gesture.None) { CancelGesture(); Raise(); return true; }
            if (Selection.Count > 0) { Selection.Clear(); Raise(); return true; }
            return false;
        }
        if (Selection.Count == 0) return false;
        switch (key)
        {
            case Key.Delete or Key.Back: DeleteSelection(); return true;
            case Key.Left: Nudge(-1, 0); return true;
            case Key.Right: Nudge(1, 0); return true;
            case Key.Up: Nudge(0, shift ? 12 : 1); return true;
            case Key.Down: Nudge(0, shift ? -12 : -1); return true;
        }
        return false;
    }

    public List<RollNote> SelectedNotes() => _notes.Where(n => Selection.Contains(n.Key)).ToList();

    public void DeleteSelection()
    {
        var keys = Selection.Keys.ToList();
        if (keys.Count == 0) return;
        Commit(RollOps.RemoveEdits(keys));
        Selection.Clear();
        Raise();
    }

    public void Nudge(int dStep, int dNote)
    {
        var sel = SelectedNotes();
        (dStep, dNote) = RollOps.ClampDelta(sel, Length, dStep, dNote);
        if (sel.Count == 0 || (dStep == 0 && dNote == 0)) return;
        var edits = RollOps.MoveEdits(sel, dStep, dNote);
        var res = Commit(edits);
        if (res != null) Selection.Set(RollOps.SelectionAfterMove(edits, res));
        if (dNote != 0) _host.AuditionTap(Math.Clamp(sel[0].Note + dNote, 0, 127));
        Raise();
    }

    public void CopySelection()
    {
        _clip = RollOps.Clip.From(SelectedNotes());
        SetStatus(_clip == null ? "Nothing selected" : $"Copied {_clip.Notes.Count} note(s)");
    }

    /// <summary>Pastes at the step under the mouse (the selected step when the pointer is not over the grid).</summary>
    public void Paste()
    {
        if (_clip == null) { SetStatus("Nothing copied"); return; }
        int at = _hoverStep >= 0 ? _hoverStep : SelectedStep;
        at = Math.Clamp(at, 0, Math.Max(0, Length - 1));
        var edits = RollOps.PasteEdits(_clip, at, Length);
        var res = Commit(edits);
        if (res == null) { SetStatus("Paste target is outside the track length"); return; }
        Selection.Set(edits.Where((e, i) => res.Succeeded[i]).Select(e => new NoteKey(e.Step, e.Note)));
        Raise();
    }

    public bool Undo()
    {
        var prev = History.Undo(_host.Track, _host.Capture());
        if (prev == null) { SetStatus("Nothing to undo"); return true; }
        _host.Restore(prev); _host.Committed(); Reload();
        SetStatus("Undo");
        return true;
    }

    public bool Redo()
    {
        var next = History.Redo(_host.Track, _host.Capture());
        if (next == null) { SetStatus("Nothing to redo"); return true; }
        _host.Restore(next); _host.Committed(); Reload();
        SetStatus("Redo");
        return true;
    }

    /// <summary>Sets the probability of the step(s) holding the selected notes (the selected step when nothing is selected).</summary>
    public void SetSelectionProbability(int percent)
    {
        var steps = Selection.Count > 0 ? SelectedNotes().Select(n => n.Step).Distinct().ToList() : [SelectedStep];
        var before = _host.Capture();
        foreach (var s in steps) _host.SetProbability(s, percent);
        History.Push(_host.Track, before);
        _host.Committed();
        Reload();
    }

    /// <summary>One-line description of the selection for the info bar.</summary>
    public string InfoText()
    {
        var sel = SelectedNotes();
        if (sel.Count == 0) return "";
        if (sel.Count > 1) return $"{sel.Count} notes selected";
        var n = sel[0];
        int prob = _snap != null && n.Step < _snap.Steps.Count ? _snap.Steps[n.Step].Probability : 100;
        return $"{NoteNames.Name(n.Note)} · step {n.Step + 1} · vel {n.Velocity} · tie {n.Tie}" + (prob < 100 ? $" · {prob}%" : "");
    }
}
