using Avalonia;
using Avalonia.Input;
using Sambient.App.PianoRoll;
using Sambient.Engine;
using Sambient.Seq;
using Xunit;

namespace Sambient.App.Tests;

/// <summary>An in-memory host: a TrackData edited through the real <see cref="NoteEditing"/> rules (no audio engine).</summary>
internal sealed class FakeRollHost : IRollHost
{
    public TrackData Data = new();
    public int Length = 16;
    public int Track { get; set; }
    public int Commits, SelectedStep = -1;
    public List<string> Audition = new();

    public TrackSnapshot GetTrack()
        => new(Track, Length, StepNote.Sixteenth1_16, 0,
            Data.Steps.Select(s => new StepSnapshot(s.Notes.ToArray(), s.Probability, s.Locks.Select(k => new ParamLock(k.Key, k.Value)).ToArray())).ToArray());
    public NoteEditResult Apply(IReadOnlyList<NoteEdit> edits) => NoteEditing.Apply(Data, Length, edits);
    public void SetLength(int length) => Length = length;
    public void SetProbability(int step, int percent) => Data.Steps[step].Probability = percent;
    public TrackState Capture() => new(Data.Clone(), Length);
    public void Restore(TrackState s) { Data = s.Data.Clone(); Length = s.Length; }
    public void SelectStep(int step) => SelectedStep = step;
    public void Audition_(int n) { }
    void IRollHost.Audition(int note, bool on) => Audition.Add((on ? "on " : "off ") + note);
    public void AuditionTap(int note) => Audition.Add("tap " + note);
    public void Committed() => Commits++;

    public FakeRollHost Add(int step, int note, int vel = 100, int tie = 1) { Data.Steps[step].Notes.Add(StepNoteData.Make(note, vel, tie)); return this; }
    public IEnumerable<StepNoteData> At(int step) => Data.Steps[step].Notes;
}

public class RollViewportTests
{
    private static RollViewport Vp()
    {
        var vp = new RollViewport();
        vp.SetSize(400, 200);
        vp.SetScale(40, 14);
        return vp;
    }

    [Fact]
    public void StepAndPixelMapping_RoundTrips_WithScroll()
    {
        var vp = Vp();
        vp.ScrollX = 120;
        Assert.Equal(-120, vp.StepToX(0));
        Assert.Equal(3.0, vp.XToStep(0), 6);
        Assert.Equal(3.5, vp.XToStep(vp.StepToX(3.5)), 6);
        Assert.Equal(3, vp.XToStepIndex(10));
        Assert.Equal(0, vp.XToStepIndex(-500));                 // clamped
        Assert.Equal(63, vp.XToStepIndex(99999));
    }

    [Fact]
    public void RowAndPitchMapping_HighNotesAtTop()
    {
        var vp = Vp();
        vp.ScrollY = 0;
        Assert.Equal(127, vp.YToNote(0));
        Assert.Equal(126, vp.YToNote(14));
        Assert.Equal(0, vp.YToNote(99999));
        vp.ScrollY = 14 * 67;                                   // first visible row = note 60
        Assert.Equal(60, vp.YToNote(1));
        Assert.Equal(0.0, vp.NoteToY(60), 6);
        Assert.Equal(14.0, vp.NoteToY(59), 6);
    }

    [Fact]
    public void ZoomX_KeepsTheStepUnderTheAnchor()
    {
        var vp = Vp();
        vp.ScrollX = 100;
        double before = vp.XToStep(150);
        vp.ZoomX(2, 150);
        Assert.Equal(80, vp.StepW);
        Assert.Equal(before, vp.XToStep(150), 6);
        vp.ZoomX(100, 150);
        Assert.Equal(RollViewport.MaxStepW, vp.StepW);          // clamped
        vp.ZoomX(0.0001, 150);
        Assert.Equal(RollViewport.MinStepW, vp.StepW);
        Assert.Equal(0, vp.ScrollX);                            // everything fits again
    }

    [Fact]
    public void ZoomY_KeepsTheRowUnderTheAnchor_AndClampsScroll()
    {
        var vp = Vp();
        vp.ScrollY = 800;
        double row = (60 + vp.ScrollY) / vp.RowH;
        vp.ZoomY(1.5, 60);
        Assert.Equal(21, vp.RowH);
        Assert.Equal(row, (60 + vp.ScrollY) / vp.RowH, 6);
        vp.ScrollY = 1e9; vp.Clamp();
        Assert.Equal(128 * 21 - 200, vp.ScrollY);
    }

    [Fact]
    public void ScrollIsClampedToContent()
    {
        var vp = Vp();
        vp.ScrollX = -50; vp.Clamp();
        Assert.Equal(0, vp.ScrollX);
        vp.ScrollX = 1e9; vp.Clamp();
        Assert.Equal(64 * 40 - 400, vp.ScrollX);
    }

    [Fact]
    public void FitSteps_And_CenterOnNote()
    {
        var vp = Vp();
        vp.FitSteps(16);
        Assert.Equal(25, vp.StepW);
        Assert.Equal(0, vp.ScrollX);
        vp.CenterOnNote(60);
        double centre = vp.NoteToY(60) + vp.RowH / 2;
        Assert.InRange(centre, 99, 101);
    }

    [Fact]
    public void EnsureStepVisible_PagesTheView()
    {
        var vp = Vp();
        Assert.False(vp.EnsureStepVisible(3));
        Assert.True(vp.EnsureStepVisible(25));                  // 10 steps per page -> page starting at step 20
        Assert.Equal(20 * 40, vp.ScrollX);
    }

    [Fact]
    public void NoteRect_CutsTheTieAtTheTrackLength()
    {
        var vp = Vp();
        var r = vp.NoteRect(new RollNote(14, 60, 100, 8), 16)!.Value;
        Assert.Equal(14 * 40, r.X);
        Assert.Equal(2 * 40, r.Width);
        Assert.Null(RollViewport_Folded().NoteRect(new RollNote(0, 61, 100, 1), 16));
    }

    private static RollViewport RollViewport_Folded() => new() { Rows = RowMap.Folded([60, 64]) };

    [Fact]
    public void FoldedRows_ShowOnlyUsedPitches()
    {
        var rows = RowMap.Folded([64, 60, 60, 67]);
        Assert.True(rows.IsFolded);
        Assert.Equal(3, rows.Count);
        Assert.Equal(67, rows.NoteOfRow(0));
        Assert.Equal(1, rows.RowOfNote(64));
        Assert.Equal(-1, rows.RowOfNote(61));
        Assert.Equal(2, rows.NearestRow(61));
        Assert.Same(RowMap.Full, RowMap.Folded([]));
    }

    [Fact]
    public void ScaleMap_MajorAndMinorOfAKey()
    {
        var maj = ScaleMap.For(Temperament.PureMajor, 2)!;      // D major: D E F# G A B C#
        Assert.Equal([2, 4, 6, 7, 9, 11, 1], Enumerable.Range(0, 12).Where(i => maj[i]).OrderBy(i => new[] { 2, 4, 6, 7, 9, 11, 1 }.ToList().IndexOf(i)));
        var min = ScaleMap.For(Temperament.PureMinor, 9)!;      // A minor = white keys
        Assert.Equal([0, 2, 4, 5, 7, 9, 11], Enumerable.Range(0, 12).Where(i => min[i]));
        Assert.Null(ScaleMap.For(Temperament.Equal, 0));
        Assert.Null(ScaleMap.For(Temperament.Solfeggio, 0));
    }

    [Fact]
    public void GridLines_FollowTheStepLength()
    {
        Assert.Equal(4, RollGrid.StepsPerBeat(StepNote.Sixteenth1_16));
        Assert.Equal(2, RollGrid.StepsPerBeat(StepNote.Eighth1_8));
        Assert.Equal(1, RollGrid.StepsPerBeat(StepNote.Quarter1_4));
        Assert.Equal(1, RollGrid.StepsPerBeat(StepNote.Whole1_1));
        Assert.Equal(2, RollGrid.LineStrength(0, 4));
        Assert.Equal(1, RollGrid.LineStrength(4, 4));
        Assert.Equal(0, RollGrid.LineStrength(5, 4));
        Assert.Equal(2, RollGrid.LineStrength(16, 4));
    }
}

public class RollHitTestSelectionTests
{
    private static RollViewport Vp() { var v = new RollViewport(); v.SetSize(800, 300); v.SetScale(40, 14); v.ScrollY = 14 * 60; return v; }   // top row = note 67

    [Fact]
    public void BodyVersusResizeEdge()
    {
        var vp = Vp();
        var notes = new[] { new RollNote(2, 64, 100, 3) };            // x 80..200, row of 64 = 3 -> y 42..56
        var r = vp.NoteRect(notes[0], 16)!.Value;
        Assert.Equal(HitZone.Body, RollHitTest.Hit(notes, vp, 16, new Point(r.X + 10, r.Y + 5))!.Value.Zone);
        Assert.Equal(HitZone.ResizeEdge, RollHitTest.Hit(notes, vp, 16, new Point(r.Right - 3, r.Y + 5))!.Value.Zone);
        Assert.Equal(HitZone.Body, RollHitTest.Hit(notes, vp, 16, new Point(r.Right - 12, r.Y + 5))!.Value.Zone);
        Assert.Null(RollHitTest.Hit(notes, vp, 16, new Point(r.Right + 2, r.Y + 5)));
        Assert.Null(RollHitTest.Hit(notes, vp, 16, new Point(r.X + 10, r.Y - 3)));
    }

    [Fact]
    public void NarrowBars_StillHaveAGrabbableEdge_AndPastLengthNotesAreIgnored()
    {
        var vp = Vp(); vp.SetScale(6, 14);
        var n = new RollNote(2, 64, 100, 1);
        var r = vp.NoteRect(n, 16)!.Value;
        Assert.Equal(6, r.Width);
        Assert.Equal(HitZone.ResizeEdge, RollHitTest.Hit([n], vp, 16, new Point(r.Right - 1, r.Y + 3))!.Value.Zone);
        Assert.Equal(3, RollHitTest.EdgeWidth(6));
        Assert.Equal(8, RollHitTest.EdgeWidth(200));
        Assert.Null(RollHitTest.Hit([new RollNote(20, 64, 100, 1)], vp, 16, new Point(20 * 6 + 2, vp.NoteRect(new RollNote(20, 64, 1, 1), 16)!.Value.Y + 3)));
    }

    [Fact]
    public void TopmostNoteWins()
    {
        var vp = Vp();
        var a = new RollNote(2, 64, 100, 4); var b = new RollNote(3, 64, 100, 1);       // b starts inside a's bar
        var p = new Point(vp.StepToX(3.5), vp.NoteToY(64) + 5);
        Assert.Equal(b, RollHitTest.Hit([a, b], vp, 16, p)!.Value.Note);
    }

    [Fact]
    public void Marquee_SelectsTouchedNotes_AdditiveKeepsBaseline()
    {
        var vp = Vp();
        var notes = new[] { new RollNote(1, 64, 100, 1), new RollNote(5, 64, 100, 1), new RollNote(3, 60, 100, 1), new RollNote(20, 64, 100, 1) };
        var sel = new RollSelection();
        double y64 = vp.NoteToY(64), y60 = vp.NoteToY(60);
        sel.Marquee(notes, vp, 16, new Rect(30, y64 - 2, 200, 16), null);              // steps 0..5 on the 64 row
        Assert.Equal([new NoteKey(1, 64), new NoteKey(5, 64)], sel.Keys.OrderBy(k => k.Step));
        sel.Marquee(notes, vp, 16, new Rect(100, y60, 30, 14), [new NoteKey(1, 64)]);
        Assert.Equal([new NoteKey(1, 64), new NoteKey(3, 60)], sel.Keys.OrderBy(k => k.Step));
        sel.Marquee(notes, vp, 16, new Rect(0, 0, 800, 300), null);
        Assert.Equal(3, sel.Count);                                                    // the note past the length is never selected
    }

    [Fact]
    public void SelectionToggle_Prune_SelectAll()
    {
        var notes = new[] { new RollNote(0, 60, 100, 1), new RollNote(1, 62, 100, 1), new RollNote(30, 64, 100, 1) };
        var sel = new RollSelection();
        sel.SelectAll(notes, 16);
        Assert.Equal(2, sel.Count);
        sel.Toggle(new NoteKey(0, 60));
        Assert.False(sel.Contains(new NoteKey(0, 60)));
        sel.Toggle(new NoteKey(0, 60));
        sel.Prune(notes.Skip(1));
        Assert.Equal([new NoteKey(1, 62)], sel.Keys);
    }
}

public class RollOpsTests
{
    [Fact]
    public void ClampDelta_KeepsTheGroupInsideTheTrackAndPitchRange()
    {
        RollNote[] sel = [new(2, 60, 100, 1), new(5, 70, 100, 1)];
        Assert.Equal((-2, 0), RollOps.ClampDelta(sel, 16, -10, 0));
        Assert.Equal((10, 0), RollOps.ClampDelta(sel, 16, 20, 0));
        Assert.Equal((0, -60), RollOps.ClampDelta(sel, 16, 0, -100));
        Assert.Equal((0, 57), RollOps.ClampDelta(sel, 16, 0, 100));
        Assert.Equal((0, 0), RollOps.ClampDelta([], 16, 3, 3));
    }

    [Fact]
    public void TieFromPosition_SnapsToGridLines_NeverBelowOneNorPastTheEnd()
    {
        Assert.Equal(1, RollOps.TieFromPosition(3, 3.4, 16));
        Assert.Equal(1, RollOps.TieFromPosition(3, 1.0, 16));
        Assert.Equal(3, RollOps.TieFromPosition(3, 6.2, 16));
        Assert.Equal(2, RollOps.TieFromPosition(3, 5.4, 16));
        Assert.Equal(13, RollOps.TieFromPosition(3, 40, 16));
    }

    [Fact]
    public void VelocityFromY_TopIs127_BottomIs1()
    {
        Assert.Equal(127, RollOps.VelocityFromY(4, 4, 59));
        Assert.Equal(1, RollOps.VelocityFromY(63, 4, 59));
        Assert.Equal(1, RollOps.VelocityFromY(500, 4, 59));
        Assert.InRange(RollOps.VelocityFromY(33.5, 4, 59), 62, 66);
    }

    [Fact]
    public void Clip_IsRelativeToItsFirstStep_PasteDropsNotesPastTheEnd()
    {
        var clip = RollOps.Clip.From([new RollNote(6, 60, 90, 2), new RollNote(8, 64, 80, 1)])!;
        Assert.Equal([0, 2], clip.Notes.Select(n => n.Step));
        var edits = RollOps.PasteEdits(clip, 14, 16);
        Assert.Single(edits);
        Assert.Equal(NoteEdit.Add(14, 60, 90, 2), edits[0]);
        Assert.Null(RollOps.Clip.From([]));
    }
}

public class RollHistoryTests
{
    private static TrackState S(int len) => new(new TrackData(), len);

    [Fact]
    public void UndoRedo_WalkBackAndForth_PerTrack()
    {
        var h = new RollHistory();
        h.Push(0, S(1)); h.Push(0, S(2)); h.Push(1, S(9));
        var u = h.Undo(0, S(3))!;
        Assert.Equal(2, u.Length);
        Assert.Equal(3, h.Redo(0, S(2))!.Length);
        Assert.Equal(2, h.Undo(0, S(3))!.Length);
        Assert.Equal(1, h.Undo(0, S(2))!.Length);
        Assert.Null(h.Undo(0, S(1)));
        Assert.True(h.CanUndo(1));
    }

    [Fact]
    public void NewEdit_ClearsRedo_AndDepthIsBounded()
    {
        var h = new RollHistory();
        h.Push(0, S(1));
        h.Undo(0, S(2));
        Assert.True(h.CanRedo(0));
        h.Push(0, S(5));
        Assert.False(h.CanRedo(0));
        for (int i = 0; i < 80; i++) h.Push(0, S(i + 1));
        Assert.Equal(RollHistory.MaxDepth, h.UndoDepth(0));
        h.Clear();
        Assert.False(h.CanUndo(0));
    }
}

public class PianoRollEditorTests
{
    private static (PianoRollEditor ed, FakeRollHost host) Make(int length = 16, Action<FakeRollHost>? setup = null)
    {
        var host = new FakeRollHost { Length = length };
        setup?.Invoke(host);
        var ed = new PianoRollEditor(host);
        ed.SetSize(754, 400);
        ed.SetTrack(host.GetTrack());
        ed.View.SetScale(40, 14);
        ed.View.ScrollX = 0;
        ed.View.ScrollY = 14 * (127 - 72);                       // top row = note 72
        return (ed, host);
    }

    private static Point P(PianoRollEditor ed, double step, int note)
        => new(ed.GridRect.X + ed.View.StepToX(step), ed.GridRect.Y + ed.View.NoteToY(note) + 7);

    private static void Click(PianoRollEditor ed, Point p, KeyModifiers m = KeyModifiers.None, RollButton b = RollButton.Left)
    {
        ed.PointerPressed(p, m, b, 1);
        ed.PointerReleased(p, m);
    }

    private static void Drag(PianoRollEditor ed, Point from, Point to, KeyModifiers m = KeyModifiers.None, RollButton b = RollButton.Left)
    {
        ed.PointerPressed(from, m, b, 1);
        int n = 8;
        for (int i = 1; i <= n; i++) ed.PointerMoved(new Point(from.X + (to.X - from.X) * i / n, from.Y + (to.Y - from.Y) * i / n), m);
        ed.PointerReleased(to, m);
    }

    [Fact]
    public void ClickOnEmptyCell_AddsANoteWithLastLengthAndVelocity()
    {
        var (ed, host) = Make();
        ed.LastTie = 2; ed.LastVelocity = 77;
        Click(ed, P(ed, 3.5, 64));
        Assert.Equal(new StepNoteData(64, 77, 2), host.At(3).Single());
        Assert.Contains(new NoteKey(3, 64), ed.Selection.Keys);
        Assert.Equal(3, host.SelectedStep);
        Assert.Equal(1, host.Commits);
        Assert.Equal(["on 64", "off 64"], host.Audition);
        Assert.Equal(2, ed.LastTie);                                 // a click does not change the remembered length
    }

    [Fact]
    public void DrawDrag_SetsTheLength_AndRemembersIt()
    {
        var (ed, host) = Make();
        Drag(ed, P(ed, 2.2, 60), P(ed, 5.2, 60));                    // end near grid line 5 -> tie 3
        Assert.Equal(3, host.At(2).Single().Tie);
        Assert.Equal(3, ed.LastTie);
        Click(ed, P(ed, 8.5, 62));
        Assert.Equal(3, host.At(8).Single().Tie);                    // next click reuses it
    }

    [Fact]
    public void AddNearTheTrackEnd_ClampsTheTie_ClickPastTheEndIsRejected()
    {
        var (ed, host) = Make();
        ed.View.SetScale(20, 14);
        ed.LastTie = 8;
        Click(ed, P(ed, 14.5, 60));
        Assert.Equal(2, host.At(14).Single().Tie);
        Click(ed, P(ed, 20.5, 60));                                  // greyed area
        Assert.Empty(host.At(20));
        Assert.Contains("outside", ed.StatusText);
    }

    [Fact]
    public void DragNoteBody_MovesInStepsAndSemitones_KeepingVelocityAndTie()
    {
        var (ed, host) = Make(16, h => h.Add(2, 60, 90, 3));
        Drag(ed, P(ed, 2.5, 60), P(ed, 6.5, 64));
        Assert.Empty(host.At(2));
        Assert.Equal(new StepNoteData(64, 90, 3), host.At(6).Single());
        Assert.Contains(new NoteKey(6, 64), ed.Selection.Keys);
        Assert.Contains("tap 64", host.Audition);
    }

    [Fact]
    public void SmallJitterWhileClicking_DoesNotMove()
    {
        var (ed, host) = Make(16, h => h.Add(2, 60));
        var p = P(ed, 2.5, 60);
        ed.PointerPressed(p, KeyModifiers.None, RollButton.Left, 1);
        ed.PointerMoved(new Point(p.X + 1, p.Y + 1), KeyModifiers.None);
        ed.PointerReleased(p, KeyModifiers.None);
        Assert.Single(host.At(2));
        Assert.Equal(0, host.Commits);
        Assert.Contains(new NoteKey(2, 60), ed.Selection.Keys);
    }

    [Fact]
    public void DragRightEdge_ResizesTheTie_AndClampsAtTrackEnd()
    {
        var (ed, host) = Make(16, h => h.Add(2, 60, 100, 2));
        double rightEdge = ed.View.StepToX(4) + ed.GridRect.X - 2;
        var from = new Point(rightEdge, P(ed, 0, 60).Y);
        Drag(ed, from, new Point(from.X + 40 * 3, from.Y));          // +3 steps
        Assert.Equal(5, host.At(2).Single().Tie);
        Drag(ed, new Point(ed.View.StepToX(7) + ed.GridRect.X - 2, from.Y), new Point(from.X + 2000, from.Y));
        Assert.Equal(14, host.At(2).Single().Tie);                   // 16 - 2
    }

    [Fact]
    public void ResizingASelectedNote_ResizesTheWholeSelection()
    {
        var (ed, host) = Make(16, h => h.Add(2, 60, 100, 2).Add(6, 64, 100, 1));
        ed.KeyDown(Key.A, KeyModifiers.Meta);
        double edge = ed.View.StepToX(4) + ed.GridRect.X - 2;
        var from = new Point(edge, P(ed, 0, 60).Y);
        Drag(ed, from, new Point(from.X + 40 * 2, from.Y));
        Assert.Equal(4, host.At(2).Single().Tie);
        Assert.Equal(3, host.At(6).Single().Tie);
    }

    [Fact]
    public void RightClick_DeletesANote_RightDragSweepsSeveral()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60).Add(2, 62).Add(3, 64).Add(4, 66));
        Click(ed, P(ed, 1.5, 60), b: RollButton.Right);
        Assert.Empty(host.At(1));
        Drag(ed, P(ed, 2.5, 62), P(ed, 4.5, 66), b: RollButton.Right);      // diagonal sweep over 62, 64, 66
        Assert.Empty(host.At(2)); Assert.Empty(host.At(3)); Assert.Empty(host.At(4));
        Assert.Equal(2, host.Commits);                                       // one undo step per gesture
        ed.Undo();
        Assert.Equal(3, host.Data.Steps.Count(s => s.Notes.Count > 0));
    }

    [Fact]
    public void EraseTool_AndAltClick_Delete()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60).Add(2, 62));
        ed.Tool = RollTool.Erase;
        Click(ed, P(ed, 1.5, 60));
        Assert.Empty(host.At(1));
        ed.Tool = RollTool.Draw;
        Click(ed, P(ed, 2.5, 62), KeyModifiers.Alt);
        Assert.Empty(host.At(2));
    }

    [Fact]
    public void ShiftClick_AddsToSelection_AndShiftClickAgainRemoves()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60).Add(2, 62));
        Click(ed, P(ed, 1.5, 60));
        Click(ed, P(ed, 2.5, 62), KeyModifiers.Shift);
        Assert.Equal(2, ed.Selection.Count);
        Click(ed, P(ed, 1.5, 60), KeyModifiers.Shift);
        Assert.Equal([new NoteKey(2, 62)], ed.Selection.Keys);
        Click(ed, P(ed, 1.5, 60));                                           // plain click: only that note
        Assert.Equal([new NoteKey(1, 60)], ed.Selection.Keys);
    }

    [Fact]
    public void MarqueeWithCtrl_SelectsInDrawTool_SelectToolMarqueeOnPlainDrag()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60).Add(2, 64).Add(9, 60));
        Drag(ed, P(ed, 0.1, 66), P(ed, 3.5, 58), KeyModifiers.Control);
        Assert.Equal(2, ed.Selection.Count);
        Assert.Equal(0, host.Commits);
        ed.Tool = RollTool.Select;
        Drag(ed, P(ed, 8, 62), P(ed, 10, 58));
        Assert.Equal([new NoteKey(9, 60)], ed.Selection.Keys);
        Click(ed, P(ed, 5, 70));                                             // empty click clears
        Assert.Equal(0, ed.Selection.Count);
    }

    [Fact]
    public void GroupMove_ShiftsTheWholeSelection()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60, 100, 2).Add(2, 64).Add(3, 67));
        ed.KeyDown(Key.A, KeyModifiers.Meta);
        Drag(ed, P(ed, 1.5, 60), P(ed, 3.5, 62));                            // +2 steps, +2 semitones
        Assert.Equal(new StepNoteData(62, 100, 2), host.At(3).Single(n => n.Note == 62));
        Assert.Equal(66, host.At(4).Single().Note);
        Assert.Equal(69, host.At(5).Single().Note);
        Assert.Equal(3, ed.Selection.Count);
        Assert.Contains(new NoteKey(5, 69), ed.Selection.Keys);
    }

    [Fact]
    public void GroupMove_IsClampedAtTheEdges()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60).Add(14, 62));
        ed.KeyDown(Key.A, KeyModifiers.Meta);
        Drag(ed, P(ed, 1.5, 60), P(ed, 10.5, 60));                           // would be +9, limited to +1 by the note on step 14
        Assert.Single(host.At(2)); Assert.Single(host.At(15));
    }

    [Fact]
    public void GroupMove_WithConflict_BlockedNoteStaysAndStatusExplains()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60).Add(1, 64).Add(2, 64));
        Click(ed, P(ed, 1.5, 60));
        ed.KeyDown(Key.A, KeyModifiers.Meta);
        ed.Selection.Set([new NoteKey(1, 60), new NoteKey(1, 64)]);
        ed.Nudge(1, 0);
        Assert.Equal([64], host.At(1).Select(n => n.Note));                  // 64 -> step 2 is taken: stays
        Assert.Equal([60, 64], host.At(2).Select(n => n.Note).Order());
        Assert.Contains("already sounds", ed.StatusText);
        Assert.Equal([new NoteKey(2, 60), new NoteKey(1, 64)], ed.Selection.Keys.ToArray());
    }

    [Fact]
    public void EightNoteLimit_ShowsAStatusMessage()
    {
        var (ed, host) = Make(16, h => { for (int i = 0; i < 8; i++) h.Add(3, 50 + i); });
        Click(ed, P(ed, 4.5, 70));                                           // step 5 is free
        ed.Selection.Set([new NoteKey(4, 70)]);
        ed.Nudge(-1, 0);                                                     // move onto the full step 4
        Assert.Contains("8 notes", ed.StatusText);
        Assert.Single(host.At(4));
        Click(ed, P(ed, 3.5, 70));                                           // draw on the full step
        Assert.Equal(8, host.At(3).Count());
        Assert.Contains("8 notes", ed.StatusText);
    }

    [Fact]
    public void Keyboard_DeleteSelectAllNudgeAndOctave()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60).Add(2, 64));
        Assert.False(ed.KeyDown(Key.Delete, KeyModifiers.None));             // nothing selected: global shortcut stays
        Assert.False(ed.KeyDown(Key.Left, KeyModifiers.None));
        Assert.True(ed.KeyDown(Key.A, KeyModifiers.Meta));
        Assert.Equal(2, ed.Selection.Count);
        Assert.True(ed.KeyDown(Key.Up, KeyModifiers.Shift));
        Assert.Equal(72, host.At(1).Single().Note);
        Assert.True(ed.KeyDown(Key.Right, KeyModifiers.None));
        Assert.Equal(72, host.At(2).Single().Note);
        Assert.Equal(76, host.At(3).Single().Note);
        Assert.True(ed.KeyDown(Key.Down, KeyModifiers.None));
        Assert.Equal(71, host.At(2).Single().Note);
        Assert.True(ed.KeyDown(Key.Back, KeyModifiers.None));
        Assert.All(host.Data.Steps, s => Assert.Empty(s.Notes));
        Assert.False(ed.KeyDown(Key.Space, KeyModifiers.None));              // global
        Assert.False(ed.KeyDown(Key.R, KeyModifiers.None));
    }

    [Fact]
    public void CopyPaste_PastesAtTheStepUnderTheMouse()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60, 100, 2).Add(3, 64));
        ed.KeyDown(Key.A, KeyModifiers.Meta);
        ed.KeyDown(Key.C, KeyModifiers.Meta);
        ed.PointerMoved(P(ed, 8.5, 56), KeyModifiers.None);                  // hover step 9 (index 8)
        ed.KeyDown(Key.V, KeyModifiers.Meta);
        Assert.Equal(new StepNoteData(60, 100, 2), host.At(8).Single());
        Assert.Equal(64, host.At(10).Single().Note);
        Assert.Equal([new NoteKey(8, 60), new NoteKey(10, 64)], ed.Selection.Keys.OrderBy(k => k.Step));
        Assert.Single(host.At(1));                                            // originals stay
    }

    [Fact]
    public void UndoRedo_ThroughTheKeyboard_RestoreNotesAndLength()
    {
        var (ed, host) = Make(16);
        Click(ed, P(ed, 1.5, 60));
        Click(ed, P(ed, 2.5, 62));
        Assert.True(ed.KeyDown(Key.Z, KeyModifiers.Meta));
        Assert.Empty(host.At(2)); Assert.Single(host.At(1));
        Assert.True(ed.KeyDown(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift));
        Assert.Single(host.At(2));
        ed.KeyDown(Key.Z, KeyModifiers.Meta); ed.KeyDown(Key.Z, KeyModifiers.Meta);
        Assert.All(host.Data.Steps, s => Assert.Empty(s.Notes));
        ed.KeyDown(Key.Z, KeyModifiers.Meta);
        Assert.Contains("Nothing to undo", ed.StatusText);
        Assert.True(host.Commits >= 6);                                       // every undo / redo is a committed change (project dirty)
    }

    [Fact]
    public void LengthMarker_DragChangesTheLength_AndIsUndoable()
    {
        var (ed, host) = Make(16);
        double markerX = ed.GridRect.X + ed.View.StepToX(16);
        Drag(ed, new Point(markerX + 1, 10), new Point(ed.GridRect.X + ed.View.StepToX(12) + 3, 10));
        Assert.Equal(12, host.Length);
        ed.Undo();
        Assert.Equal(16, host.Length);
    }

    [Fact]
    public void RulerClick_SelectsTheStep_PastLengthIsIgnored()
    {
        var (ed, host) = Make(16);
        Click(ed, new Point(ed.GridRect.X + ed.View.StepToX(5.5), 10));
        Assert.Equal(5, host.SelectedStep);
        host.SelectedStep = -1;
        Click(ed, new Point(ed.GridRect.X + ed.View.StepToX(30.5), 10));
        Assert.Equal(-1, host.SelectedStep);
    }

    [Fact]
    public void VelocityLane_PaintsAcrossBars()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60, 100).Add(2, 60, 100).Add(3, 60, 100).Add(4, 60, 100));
        double x(double step) => ed.VelRect.X + ed.View.StepToX(step);
        double y(int vel) => ed.VelRect.Y + RollVelocity.PadTop + RollVelocity.UsableHeight(PianoRollEditor.VelH) * (1 - (vel - 1) / 126.0);
        ed.PointerPressed(new Point(x(1.5), y(20)), KeyModifiers.None, RollButton.Left, 1);
        ed.PointerMoved(new Point(x(3.5), y(120)), KeyModifiers.None);
        ed.PointerReleased(new Point(x(3.5), y(120)), KeyModifiers.None);
        Assert.InRange(host.At(1).Single().Velocity, 15, 25);
        Assert.InRange(host.At(2).Single().Velocity, 55, 85);                 // interpolated in between
        Assert.InRange(host.At(3).Single().Velocity, 110, 127);
        Assert.Equal(100, host.At(4).Single().Velocity);                      // untouched
        Assert.Equal(1, host.Commits);
        Assert.InRange(ed.LastVelocity, 110, 127);
        ed.Undo();
        Assert.Equal(100, host.At(1).Single().Velocity);
    }

    [Fact]
    public void KeyStrip_Auditions_AndGlisses()
    {
        var (ed, host) = Make();
        var p = new Point(10, ed.KeysRect.Y + ed.View.NoteToY(64) + 5);
        ed.PointerPressed(p, KeyModifiers.None, RollButton.Left, 1);
        ed.PointerMoved(new Point(10, ed.KeysRect.Y + ed.View.NoteToY(62) + 5), KeyModifiers.None);
        ed.PointerReleased(p, KeyModifiers.None);
        Assert.Equal(["on 64", "off 64", "on 62", "off 62"], host.Audition);
    }

    [Fact]
    public void Wheel_ZoomsAndScrolls()
    {
        var (ed, _) = Make();
        double w = ed.View.StepW;
        ed.Wheel(new Point(300, 100), new Vector(0, 1), KeyModifiers.Meta);
        Assert.True(ed.View.StepW > w);
        double h = ed.View.RowH;
        ed.Wheel(new Point(300, 100), new Vector(0, 1), KeyModifiers.Alt);
        Assert.True(ed.View.RowH > h);
        double sy = ed.View.ScrollY;
        ed.Wheel(new Point(300, 100), new Vector(0, -1), KeyModifiers.None);
        Assert.True(ed.View.ScrollY > sy);
    }

    [Fact]
    public void PlayheadFollow_PagesTheView()
    {
        var (ed, _) = Make(64);
        ed.SetPlayhead(40.3, true);
        Assert.True(ed.View.StepToX(40.3) >= 0 && ed.View.StepToX(40.3) < ed.View.GridW);
        ed.SetPlayhead(2.5, false);
        Assert.True(ed.View.ScrollX > 0);                                     // without follow the view stays
    }

    [Fact]
    public void Fold_RebuildsRowsFromTheNotes()
    {
        var (ed, _) = Make(16, h => h.Add(1, 60).Add(2, 67));
        ed.Fold = true;
        Assert.Equal(2, ed.View.Rows.Count);
        Assert.Equal(67, ed.View.Rows.NoteOfRow(0));
        ed.Fold = false;
        Assert.Equal(128, ed.View.Rows.Count);
    }

    [Fact]
    public void ProbabilityAndInfoText_FollowTheSelection()
    {
        var (ed, host) = Make(16, h => h.Add(2, 64, 99, 3));
        Click(ed, P(ed, 2.5, 64));
        Assert.Equal("E4 · step 3 · vel 99 · tie 3", ed.InfoText());
        ed.SetSelectionProbability(50);
        Assert.Equal(50, host.Data.Steps[2].Probability);
        Assert.EndsWith("· 50%", ed.InfoText());
        ed.Undo();
        Assert.Equal(100, host.Data.Steps[2].Probability);
    }

    [Fact]
    public void ChangingTheTrack_ResetsSelectionAndView()
    {
        var (ed, host) = Make(16, h => h.Add(1, 60));
        Click(ed, P(ed, 1.5, 60));
        host.Track = 2;
        ed.SetTrack(host.GetTrack());
        Assert.Equal(0, ed.Selection.Count);
    }

    [Fact]
    public void DoubleClickOnANote_SelectsItsStep_AndRaisesTheEvent()
    {
        var (ed, host) = Make(16, h => h.Add(4, 60));
        RollNote? got = null;
        ed.NoteActivated += n => got = n;
        ed.PointerPressed(P(ed, 4.5, 60), KeyModifiers.None, RollButton.Left, 2);
        ed.PointerReleased(P(ed, 4.5, 60), KeyModifiers.None);
        Assert.Equal(4, host.SelectedStep);
        Assert.Equal(60, got!.Value.Note);
    }

    [Fact]
    public void Escape_CancelsAGestureWithoutCommitting()
    {
        var (ed, host) = Make(16, h => h.Add(2, 60));
        var p = P(ed, 2.5, 60);
        ed.PointerPressed(p, KeyModifiers.None, RollButton.Left, 1);
        ed.PointerMoved(P(ed, 6.5, 64), KeyModifiers.None);
        Assert.True(ed.KeyDown(Key.Escape, KeyModifiers.None));
        ed.PointerReleased(p, KeyModifiers.None);
        Assert.Single(host.At(2));
        Assert.Equal(0, host.Commits);
    }
}
