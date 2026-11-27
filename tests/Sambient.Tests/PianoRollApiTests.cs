using Sambient.Engine;
using Sambient.Seq;
using Xunit;

namespace Sambient.Tests;

public class PianoRollApiTests
{
    private static StepNoteData N(int step, SeqRig r, int note) => r.Seq.GetStep(0, step).Notes.Single(n => n.Note == note);

    [Fact]
    public void RemoveStepNote_RemovesOnlyThatNote()
    {
        using var r = new SeqRig();
        r.Seq.AddStepNote(0, 2, 60); r.Seq.AddStepNote(0, 2, 64);
        Assert.True(r.Seq.RemoveStepNote(0, 2, 60));
        Assert.Equal([64], r.Seq.GetStep(0, 2).Notes.Select(n => n.Note));
        Assert.False(r.Seq.RemoveStepNote(0, 2, 60));                 // already gone: no toggle back on
        Assert.Single(r.Seq.GetStep(0, 2).Notes);
    }

    [Fact]
    public void MoveNote_KeepsVelocityAndTie_ClampsTieAtTrackEnd()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 8);
        r.Seq.AddStepNote(0, 1, 60, 90, 4);
        Assert.True(r.Seq.MoveNote(0, 1, 60, 3, 67));
        Assert.Empty(r.Seq.GetStep(0, 1).Notes);
        Assert.Equal(new StepNoteData(67, 90, 4), N(3, r, 67));
        Assert.True(r.Seq.MoveNote(0, 3, 67, 6, 67));                 // only 2 steps left
        Assert.Equal(new StepNoteData(67, 90, 2), N(6, r, 67));
    }

    [Fact]
    public void MoveNote_Conflicts_FailAndChangeNothing()
    {
        using var r = new SeqRig();
        r.Seq.AddStepNote(0, 0, 60);
        r.Seq.AddStepNote(0, 1, 60);
        Assert.False(r.Seq.MoveNote(0, 0, 60, 1, 60));                // same pitch on target
        Assert.False(r.Seq.MoveNote(0, 5, 60, 6, 60));                // source missing
        Assert.False(r.Seq.MoveNote(0, 0, 60, 2, 128));              // pitch out of range
        Assert.False(r.Seq.MoveNote(0, 0, 60, 64, 60));              // step out of range
        for (int n = 0; n < SeqLimits.MaxNotesPerStep; n++) r.Seq.AddStepNote(0, 9, 40 + n);
        Assert.False(r.Seq.MoveNote(0, 0, 60, 9, 80));                // target step full
        Assert.Single(r.Seq.GetStep(0, 0).Notes);
        Assert.Equal(SeqLimits.MaxNotesPerStep, r.Seq.GetStep(0, 9).Notes.Count);
        Assert.True(r.Seq.MoveNote(0, 0, 60, 0, 60));                 // onto itself: no-op success
        Assert.True(r.Seq.MoveNote(0, 0, 60, 0, 62));                 // same step, other pitch
        Assert.Equal([62], r.Seq.GetStep(0, 0).Notes.Select(n => n.Note));
    }

    [Fact]
    public void SetNoteTie_And_Velocity()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 16);
        r.Seq.AddStepNote(0, 14, 60, 100, 1);
        Assert.True(r.Seq.SetNoteTie(0, 14, 60, 8));
        Assert.Equal(2, N(14, r, 60).Tie);                            // clamped, no wrap
        Assert.True(r.Seq.SetNoteTie(0, 14, 60, 0));
        Assert.Equal(1, N(14, r, 60).Tie);
        Assert.True(r.Seq.SetNoteVelocity(0, 14, 60, 500));
        Assert.Equal(127, N(14, r, 60).Velocity);
        Assert.True(r.Seq.SetNoteVelocity(0, 14, 60, -3));
        Assert.Equal(1, N(14, r, 60).Velocity);
        Assert.False(r.Seq.SetNoteTie(0, 14, 61, 2));
        Assert.False(r.Seq.SetNoteVelocity(0, 3, 60, 50));
    }

    [Fact]
    public void Batch_GroupShift_OntoVacatedPlaces_Works()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 16);
        r.Seq.AddStepNote(0, 0, 60, 100, 2); r.Seq.AddStepNote(0, 1, 60, 80, 1); r.Seq.AddStepNote(0, 2, 60, 60, 1);
        var res = r.Seq.ApplyNoteEdits(0, [NoteEdit.Move(0, 60, 1, 60), NoteEdit.Move(1, 60, 2, 60), NoteEdit.Move(2, 60, 3, 60)]);
        Assert.True(res.AllSucceeded);
        Assert.Empty(r.Seq.GetStep(0, 0).Notes);
        Assert.Equal(new StepNoteData(60, 100, 2), N(1, r, 60));
        Assert.Equal(80, N(2, r, 60).Velocity);
        Assert.Equal(60, N(3, r, 60).Velocity);
    }

    [Fact]
    public void Batch_ConflictingMove_IsPinnedAtSource_AndBlocksDependents()
    {
        using var r = new SeqRig();
        r.Seq.AddStepNote(0, 0, 60); r.Seq.AddStepNote(0, 1, 60); r.Seq.AddStepNote(0, 2, 60); r.Seq.AddStepNote(0, 3, 60);
        // 0->1 and 1->2 shift the group, but 2->3 only if 3 vacated; 3 is outside the batch and stays => 2->3 fails => 1->2 fails => 0->1 fails
        var res = r.Seq.ApplyNoteEdits(0, [NoteEdit.Move(0, 60, 1, 60), NoteEdit.Move(1, 60, 2, 60), NoteEdit.Move(2, 60, 3, 60)]);
        Assert.Equal([false, false, false], res.Succeeded);
        for (int s = 0; s < 4; s++) Assert.Single(r.Seq.GetStep(0, s).Notes);
    }

    [Fact]
    public void Batch_PartialConflict_OnlyBlockedNotesStay()
    {
        using var r = new SeqRig();
        r.Seq.AddStepNote(0, 0, 60); r.Seq.AddStepNote(0, 0, 64); r.Seq.AddStepNote(0, 1, 64);   // 64 on step 1 is outside the batch
        var res = r.Seq.ApplyNoteEdits(0, [NoteEdit.Move(0, 60, 1, 60), NoteEdit.Move(0, 64, 1, 64)]);
        Assert.Equal([true, false], res.Succeeded);
        Assert.Equal([64], r.Seq.GetStep(0, 0).Notes.Select(n => n.Note));
        Assert.Equal([60, 64], r.Seq.GetStep(0, 1).Notes.Select(n => n.Note).Order());
    }

    [Fact]
    public void Batch_MixedEdits_AreOneAtomicPublish()
    {
        using var r = new SeqRig();
        r.Seq.AddStepNote(0, 0, 60, 100, 1); r.Seq.AddStepNote(0, 1, 62); r.Seq.AddStepNote(0, 2, 64);
        var res = r.Seq.ApplyNoteEdits(0,
        [
            NoteEdit.Remove(0, 60), NoteEdit.Add(3, 72, 90, 3), NoteEdit.SetTie(1, 62, 4), NoteEdit.SetVelocity(2, 64, 33), NoteEdit.Remove(9, 1),
        ]);
        Assert.Equal([true, true, true, true, false], res.Succeeded);
        Assert.Equal(4, res.Applied); Assert.Equal(1, res.Failed);
        Assert.Equal(new StepNoteData(72, 90, 3), N(3, r, 72));
        Assert.Equal(4, N(1, r, 62).Tie);
        Assert.Equal(33, N(2, r, 64).Velocity);
        Assert.Empty(r.Seq.GetStep(0, 0).Notes);
    }

    [Fact]
    public void Batch_Add_ExistingPitchUpdates_FullStepFails()
    {
        using var r = new SeqRig();
        r.Seq.AddStepNote(0, 0, 60, 100, 1);
        for (int n = 0; n < SeqLimits.MaxNotesPerStep; n++) r.Seq.AddStepNote(0, 5, 30 + n);
        var res = r.Seq.ApplyNoteEdits(0, [NoteEdit.Add(0, 60, 40, 2), NoteEdit.Add(5, 90)]);
        Assert.Equal([true, false], res.Succeeded);
        Assert.Equal(new StepNoteData(60, 40, 2), N(0, r, 60));
    }

    [Fact]
    public void Batch_IsPublishedToTheAudioThread_WhilePlaying()
    {
        using var r = new SeqRig();
        r.Seq.SetTrackLength(0, 4);
        r.Seq.AddStepNote(0, 0, 60);
        r.Seq.Play();
        r.Render(2000);
        r.Seq.ApplyNoteEdits(0, [NoteEdit.Move(0, 60, 2, 67)]);
        r.Events.Clear();
        r.Render(Sequencer_FramesPerStep(r) * 8);
        Assert.Contains(r.Events, e => e.Kind == SeqTapKind.NoteOn && e.Note == 67);
        Assert.DoesNotContain(r.Events, e => e.Kind == SeqTapKind.NoteOn && e.Note == 60);
    }

    private static int Sequencer_FramesPerStep(SeqRig r) => SeqRig.Fs / 8;       // 120 BPM, 1/16 steps = 0.125 s

    [Fact]
    public void SnapshotAndRestoreTrack_RoundTrips()
    {
        using var r = new SeqRig();
        r.Seq.AddStepNote(0, 0, 60, 100, 2); r.Seq.SetStepProbability(0, 0, 50); r.Seq.SetLock(0, 1, ParamId.Cutoff, 77);
        var snap = r.Seq.SnapshotTrack(0);
        r.Seq.ClearTrack(0);
        Assert.Empty(r.Seq.GetStep(0, 0).Notes);
        r.Seq.RestoreTrack(0, snap);
        Assert.Equal(new StepNoteData(60, 100, 2), N(0, r, 60));
        Assert.Equal(50, r.Seq.GetStep(0, 0).Probability);
        Assert.Single(r.Seq.GetStep(0, 1).Locks);
        r.Seq.ClearTrack(0);                                          // the snapshot is independent of later edits
        r.Seq.RestoreTrack(0, snap);
        Assert.Single(r.Seq.GetStep(0, 0).Notes);
    }
}
