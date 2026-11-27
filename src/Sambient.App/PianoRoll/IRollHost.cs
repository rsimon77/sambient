using Sambient.Seq;

namespace Sambient.App.PianoRoll;

/// <summary>What the piano-roll editor needs from the application: the edited track, atomic edits, undo snapshots, audition and project bookkeeping.</summary>
public interface IRollHost
{
    /// <summary>The track (layer index) being edited.</summary>
    int Track { get; }
    TrackSnapshot GetTrack();
    NoteEditResult Apply(IReadOnlyList<NoteEdit> edits);
    void SetLength(int length);
    void SetProbability(int step, int percent);
    /// <summary>A deep copy of the track state for undo.</summary>
    TrackState Capture();
    void Restore(TrackState state);
    /// <summary>Selects a step for the step-detail / p-lock editor.</summary>
    void SelectStep(int step);
    /// <summary>Sustained audition (key strip / note being drawn).</summary>
    void Audition(int note, bool on);
    /// <summary>A short note (moved / nudged notes).</summary>
    void AuditionTap(int note);
    /// <summary>Called after every model change made through the editor (marks the project dirty, refreshes the step views).</summary>
    void Committed();
}
