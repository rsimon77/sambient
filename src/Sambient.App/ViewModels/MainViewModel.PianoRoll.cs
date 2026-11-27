using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sambient.App.PianoRoll;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.App.ViewModels;

/// <summary>
/// Piano roll wiring: the STEPS | PIANO ROLL switch, the editor of the selected layer's track, tools / fold / scale toggles, audition
/// (key strip, drawn and moved notes play on the selected layer through the engine directly, so they never step-record) and the
/// bookkeeping after an edit (project dirty, step views refreshed, undo kept per track in <see cref="PianoRollEditor.History"/>).
/// </summary>
public partial class MainViewModel
{
    private PianoRollEditor? _roll;
    private readonly Dictionary<int, (int Layer, long Start)> _auditionLayer = new();
    private readonly List<(long Due, int Layer, int Note)> _tapOffs = new();
    private int _rollScaleSig = -1;

    [ObservableProperty] private bool _pianoRollView;
    [ObservableProperty] private RollTool _rollTool = RollTool.Draw;
    [ObservableProperty] private bool _rollFold;
    [ObservableProperty] private bool _rollScale;
    [ObservableProperty] private bool _rollScaleAvailable;
    [ObservableProperty] private string _rollInfoText = "";
    [ObservableProperty] private string _rollStatusText = "";

    public bool StepsViewVisible => !PianoRollView;
    public bool IsRollDraw => RollTool == RollTool.Draw;
    public bool IsRollSelect => RollTool == RollTool.Select;
    public bool IsRollErase => RollTool == RollTool.Erase;

    /// <summary>The piano-roll editor (created on first use; it reads / edits the track of the selected layer).</summary>
    public PianoRollEditor Roll
    {
        get
        {
            if (_roll != null) return _roll;
            _roll = new PianoRollEditor(new RollHost(this)) { Tool = RollTool, Fold = RollFold, ScaleVisible = RollScale, SelectedStep = SelectedStep };
            _roll.Changed += OnRollChanged;
            return _roll;
        }
    }

    private void OnRollChanged()
    {
        var r = _roll!;
        RollInfoText = r.InfoText();
        RollStatusText = r.StatusText;
    }

    partial void OnPianoRollViewChanged(bool value)
    {
        _settings.PianoRollView = value;
        MarkSettingsDirty();
        OnPropertyChanged(nameof(StepsViewVisible));
        if (value && _lastTrack != null) Roll.SetTrack(_lastTrack);
    }

    partial void OnRollToolChanged(RollTool value)
    {
        if (_roll != null) _roll.Tool = value;
        OnPropertyChanged(nameof(IsRollDraw));
        OnPropertyChanged(nameof(IsRollSelect));
        OnPropertyChanged(nameof(IsRollErase));
    }

    partial void OnRollFoldChanged(bool value) { if (_roll != null) _roll.Fold = value; }
    partial void OnRollScaleChanged(bool value) { if (_roll != null) { _roll.ScaleVisible = value; _roll.RefreshSelection(); } }

    [RelayCommand] private void ShowStepsView() => PianoRollView = false;
    [RelayCommand] private void ShowPianoRollView() => PianoRollView = true;

    [RelayCommand]
    private void SetRollTool(string? tool)
    {
        if (Enum.TryParse<RollTool>(tool, out var t)) RollTool = t;
    }

    [RelayCommand]
    private void SetRollProbability(string? percent)
    {
        if (int.TryParse(percent, out int p)) Roll.SetSelectionProbability(p);
    }

    [RelayCommand] private void RollUndo() => Roll.Undo();
    [RelayCommand] private void RollRedo() => Roll.Redo();

    /// <summary>Called from <see cref="RefreshSteps"/>: gives the editor the fresh track snapshot.</summary>
    private void PushTrackToRoll(TrackSnapshot snap)
    {
        if (_roll == null && !PianoRollView) return;
        Roll.SelectedStep = SelectedStep;
        Roll.SetTrack(snap);
    }

    /// <summary>30 Hz: playhead, status fade, key / temperament -> scale rows, delayed note-offs of tap auditions.</summary>
    private void TickRoll(int playheadStep, TransportState state)
    {
        long now = Environment.TickCount64;
        for (int i = _tapOffs.Count - 1; i >= 0; i--)
            if (_tapOffs[i].Due <= now) { Engine.NoteOff(_tapOffs[i].Layer, _tapOffs[i].Note); _tapOffs.RemoveAt(i); }
        if (_roll == null || !PianoRollView) return;
        double pos = state == TransportState.Stopped || playheadStep < 0 ? -1 : playheadStep + Seq.StepPhase(SelIdx);
        _roll.SetPlayhead(pos, FollowPlayhead);
        _roll.Tick();
        if (_tickCount % 6 == 0) UpdateRollScale();
    }

    private void UpdateRollScale()
    {
        var t = (Temperament)Math.Clamp((int)_params.Get(GlobalParamId.Temperament), 0, TuningLabels.TemperamentNames.Length - 1);
        int key = (int)_params.GetBase(SelIdx, ParamId.TuningKey);
        int sig = (int)t * 100 + key;
        if (sig == _rollScaleSig) return;
        _rollScaleSig = sig;
        var scale = ScaleMap.For(t, key);
        RollScaleAvailable = scale != null;
        Roll.Scale = scale;
        Roll.RefreshSelection();
    }

    // ------------------------------------------------------------ audition (never records: straight to the engine)

    /// <summary>
    /// Shortest audible preview for a layer: ambient patches often have slow attacks (0.5..3+ s), so a click-length
    /// note would end before it is heard. The preview lasts at least the attack time (+ a little), 300..1500 ms.
    /// </summary>
    private long MinAuditionMs(int layer)
        => (long)Math.Clamp(Sambient.Dsp.Params.ParamCurves.AttackMs(_params.GetBase(layer, ParamId.Attack)) + 200f, 300f, 1500f);

    private void AuditionOn(int layer, int played)
    {
        _tapOffs.RemoveAll(t => t.Layer == layer && t.Note == played);   // a pending delayed note-off must not cut the new note
        Engine.NoteOn(layer, played, (int)_params.GetBase(layer, ParamId.Velocity));
    }

    private void RollAudition(int note, bool on)
    {
        int layer = SelIdx;
        int played = Math.Clamp(note + (int)MathF.Round(_params.Get(GlobalParamId.Transpose)), 0, 127);
        if (on)
        {
            if (_auditionLayer.ContainsKey(note)) return;
            _auditionLayer[note] = (layer, Environment.TickCount64);
            AuditionOn(layer, played);
        }
        else if (_auditionLayer.Remove(note, out var a))
        {
            long due = a.Start + MinAuditionMs(a.Layer);
            if (Environment.TickCount64 >= due) Engine.NoteOff(a.Layer, played);
            else _tapOffs.Add((due, a.Layer, played));                     // released quickly: keep sounding until audible
        }
    }

    private void RollAuditionTap(int note)
    {
        int layer = SelIdx;
        int played = Math.Clamp(note + (int)MathF.Round(_params.Get(GlobalParamId.Transpose)), 0, 127);
        AuditionOn(layer, played);
        _tapOffs.Add((Environment.TickCount64 + MinAuditionMs(layer), layer, played));
    }

    /// <summary>The adapter that lets the editor talk to the sequencer of the selected layer.</summary>
    private sealed class RollHost(MainViewModel vm) : IRollHost
    {
        public int Track => vm.SelIdx;
        public TrackSnapshot GetTrack() => vm.Seq.GetTrack(vm.SelIdx);
        public NoteEditResult Apply(IReadOnlyList<NoteEdit> edits) => vm.Seq.ApplyNoteEdits(vm.SelIdx, edits);
        public void SetLength(int length) => vm.SeqLengthValue = Math.Clamp(length, 1, SeqLimits.MaxSteps);
        public void SetProbability(int step, int percent) => vm.Seq.SetStepProbability(vm.SelIdx, step, percent);
        public TrackState Capture() => new(vm.Seq.SnapshotTrack(vm.SelIdx), vm.LengthNow);

        public void Restore(TrackState state)
        {
            vm.Seq.RestoreTrack(vm.SelIdx, state.Data);
            if (state.Length != vm.LengthNow) vm.SeqLengthValue = state.Length;
        }

        public void SelectStep(int step) => vm.SelectStep(step);
        public void Audition(int note, bool on) => vm.RollAudition(note, on);
        public void AuditionTap(int note) => vm.RollAuditionTap(note);

        public void Committed()
        {
            vm._session.Project.MarkDirty();           // a piano-roll edit is always an edit of the project
            vm.RefreshSteps();
        }
    }
}
