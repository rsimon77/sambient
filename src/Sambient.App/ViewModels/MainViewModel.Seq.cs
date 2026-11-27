using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sambient.Control;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.App.ViewModels;

/// <summary>Sequencer wiring: the transport bar, the step sequencer panel, step recording and direct p-locks.</summary>
public partial class MainViewModel
{
    private int _heldStep = -1;                  // step button currently pressed (-1 none); read from the MIDI thread
    private int _heldStepLayer;
    private int _tieStart = -1;
    private int _tickCount;
    private int _stepsDirty;                     // set from any thread, consumed by the UI timer
    private bool _pendingRefresh;                // a palette was loaded from the UI: refresh the controls once the audio thread applied it
    private long _pendingSince;
    private int _lastPlayhead = -2;
    private string _detailSignature = "";
    private int _settingsDirtyTicks = -1;
    private bool _stepInputPending;
    private readonly object _noteLock = new();
    private readonly List<(int Note, int Velocity)> _heldNotes = new();
    private TrackSnapshot? _lastTrack;

    internal Sequencer Seq => Volatile.Read(ref _seq);

    // ------------------------------------------------------------ observable state

    [ObservableProperty] private bool _recArmed;
    [ObservableProperty] private bool _overwriteOn;
    [ObservableProperty] private bool _prmRecOn;
    [ObservableProperty] private bool _metroOn;
    [ObservableProperty] private bool _naturalStop;
    [ObservableProperty] private bool _bpmGlobal;
    [ObservableProperty] private bool _resetReverb;
    [ObservableProperty] private bool _autoStep;
    [ObservableProperty] private bool _tieMode;
    [ObservableProperty] private bool _followPlayhead = true;
    [ObservableProperty] private int _metroVolume = 8;
    [ObservableProperty] private int _preCount = 2;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _recModeText = "";
    [ObservableProperty] private string _cycleText = "CYC –";
    [ObservableProperty] private string _paletteText = "";
    [ObservableProperty] private string _queuedText = "";
    [ObservableProperty] private bool _queuedBlinkOn;
    [ObservableProperty] private string _tieHint = "";
    [ObservableProperty] private int _selectedStep;
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private decimal? _seqLengthValue = 16;
    [ObservableProperty] private bool _rndSeqOn;
    [ObservableProperty] private decimal? _transposeValue = 0;
    [ObservableProperty] private bool _muteSeq;
    [ObservableProperty] private string _selectedStepTitle = "";
    [ObservableProperty] private string _selectedStepProb = "";

    public string BpmModeText => BpmGlobal ? "GLBL" : "PTN";
    public ObservableCollection<StepCellViewModel> StepCells { get; } = new(Enumerable.Range(0, StepGrid.StepsPerPage).Select(_ => new StepCellViewModel()));
    public ObservableCollection<SelectorItemViewModel> Pages { get; } = new();
    public ObservableCollection<StepNoteRowViewModel> StepNotes { get; } = new();
    public ObservableCollection<StepLockRowViewModel> StepLocks { get; } = new();
    public string ShortcutsHelp => ShortcutHelp.Text;

    partial void OnBpmGlobalChanged(bool value)
    {
        Seq.BpmMode = value ? BpmMode.Global : BpmMode.Pattern;
        OnPropertyChanged(nameof(BpmModeText));
        PersistProjectSettings();
    }
    partial void OnResetReverbChanged(bool value) { Seq.ResetReverbOnChange = value; PersistProjectSettings(); }
    partial void OnRecArmedChanged(bool value) => Seq.RecordArmed = value;
    partial void OnOverwriteOnChanged(bool value) => Seq.OverwriteMode = value;
    partial void OnPrmRecOnChanged(bool value) => Seq.ParamRecordArmed = value;
    partial void OnMetroOnChanged(bool value) => Seq.MetronomeOn = value;
    partial void OnMetroVolumeChanged(int value) { Seq.MetronomeVolume = value; PersistProjectSettings(); }
    partial void OnPreCountChanged(int value) { Seq.PreCountBeats = value; PersistProjectSettings(); }
    partial void OnAutoStepChanged(bool value) { _settings.AutoStep = value; MarkSettingsDirty(); }
    partial void OnFollowPlayheadChanged(bool value) { _settings.FollowPlayhead = value; MarkSettingsDirty(); }
    partial void OnTieModeChanged(bool value)
    {
        _tieStart = -1;
        TieHint = value ? "TIE: click the start step, hold a key, click the end step" : "";
    }

    partial void OnRndSeqOnChanged(bool value) { if (!_loading) Seq.SetRndSeq(value); }
    partial void OnTransposeValueChanged(decimal? value) { if (!_loading && value is { } v) Seq.SetTranspose((int)v); }
    partial void OnMuteSeqChanged(bool value)
    {
        if (_loading) return;
        Seq.MuteMode = value ? MuteMode.Sequence : MuteMode.Sound;
        PersistProjectSettings();
    }
    partial void OnSeqLengthValueChanged(decimal? value)
    {
        if (_loading || value is not { } v) return;
        Seq.SetTrackLength(SelIdx, Math.Clamp((int)v, 1, SeqLimits.MaxSteps));
        RefreshSteps();
    }

    partial void OnCurrentPageChanged(int value) => RefreshSteps();

    // ------------------------------------------------------------ settings

    // The backing fields are set directly on purpose: the generated setters would call the (not yet created) sequencer.
#pragma warning disable MVVMTK0034
    private void InitSettingsState()
    {
        var ps = _session.Project.Settings;          // project-level settings come from the open project
        _bpmGlobal = ps.BpmMode == BpmMode.Global;
        _resetReverb = ps.ResetReverbOnChange;
        _autoStep = _settings.AutoStep;
        _followPlayhead = _settings.FollowPlayhead;
        _metroVolume = ps.MetronomeVolume;
        _preCount = ps.PreCount;
        _selectedBufferFrames = _settings.AudioBufferFrames is 128 or 256 or 512 or 1024 ? _settings.AudioBufferFrames : 512;
        _showMasterHz = _settings.ShowMasterTuneHz;
        _expandSeq = _settings.IsExpanded("seq", true);
        _expandPatterns = _settings.IsExpanded("patterns", true);
        _expandChain = _settings.IsExpanded("chain", false);
        _expandTuning = _settings.IsExpanded("tuning", false);
        _expandMidi = _settings.IsExpanded("midi", false);
        for (int p = 0; p < StepGrid.PageCount; p++)
            Pages.Add(new SelectorItemViewModel(p, StepGrid.PageLabel(p), SelectPage));
        for (int b = 0; b < PatternBank.Banks; b++)
            BankTabs.Add(new SelectorItemViewModel(b, "B" + (b + 1), BrowseBank));
        for (int i = 0; i < PatternBank.SlotsPerBank; i++)
            Slots.Add(new PatternSlotViewModel(i, OnSlotClicked));
    }
#pragma warning restore MVVMTK0034

    private void MarkSettingsDirty() => _settingsDirtyTicks = 30;

    private void SaveSettingsNow()
    {
        _settingsDirtyTicks = -1;
        _settings.Save();
    }

    // ------------------------------------------------------------ sequencer lifecycle

    /// <summary>Creates the sequencer on <paramref name="engine"/>; on a rebuild the previous palette (edited state included) is carried over.</summary>
    private void AttachSequencer(AudioEngine engine)
    {
        var old = _seq;
        PatternPalette? model = null;
        int slot = -1;
        if (old != null)
        {
            old.PaletteChanged -= OnPaletteChanged;
            model = old.Capture();
            slot = old.CurrentBank >= 0 ? PatternBank.SlotIndex(old.CurrentBank, old.CurrentSlot) : -1;
        }
        var seq = new Sequencer(engine, _patternBank)
        {
            BpmMode = BpmGlobal ? BpmMode.Global : BpmMode.Pattern,
            ResetReverbOnChange = ResetReverb,
            MetronomeOn = MetroOn,
            MetronomeVolume = MetroVolume,
            PreCountBeats = PreCount,
            RecordArmed = RecArmed,
            OverwriteMode = OverwriteOn,
            ParamRecordArmed = PrmRecOn,
        };
        seq.PaletteChanged += OnPaletteChanged;
        if (model != null) { seq.LoadModel(model, slot); _pendingRefresh = true; _pendingSince = Environment.TickCount64; }
        Volatile.Write(ref _seq, seq);
        _router.Listener = _noteTap;
    }

    private void LoadStartupPalette()
    {
        try { Seq.SelectPalette(_settings.LastBank, _settings.LastSlot); }
        catch { Seq.SelectPalette(0, 0); }
        BrowsedBank = Seq.CurrentBank >= 0 ? Seq.CurrentBank : 0;
        RefreshSlotNames();
        _pendingRefresh = true; _pendingSince = Environment.TickCount64;
        RefreshSteps();
    }

    private void OnPaletteChanged() => RefreshAll();

    /// <summary>
    /// Refreshes every control from the parameter store (BASE values) after a palette was loaded or switched — without writing
    /// anything back — and requests the WAVE / noise builds of all layers.
    /// </summary>
    private void RefreshAll()
    {
        _pendingRefresh = false;
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            for (int l = 0; l < ParameterStore.NoiseLayerIndex; l++) _bank.EnsureBuiltAsync((int)_params.GetBase(l, ParamId.Wave));
            _noise.EnsureBuiltAsync((int)_params.GetBase(ParameterStore.NoiseLayerIndex, ParamId.Wave));

            MasterKnob.Load(_params.Get(GlobalParamId.MasterVolume));
            ReverbMixKnob.Load(_params.Get(GlobalParamId.ReverbMix));
            FxSpeedKnob.Load(_params.Get(GlobalParamId.FxSpeed));
            FxAmountKnob.Load(_params.Get(GlobalParamId.FxAmount));
            PatternLevelKnob.Load(_params.Get(GlobalParamId.PatternLevel));
            MasterTuneKnob.Load(_params.Get(GlobalParamId.MasterTuneCents));
            TuneAllKnob.Load(_params.Get(GlobalParamId.TuneAllCents));
            SelectedReverb = ReverbTypes[Math.Clamp((int)_params.Get(GlobalParamId.ReverbType), 0, ReverbTypes.Count - 1)];
            SelectedFx = FxTypes[Math.Clamp((int)_params.Get(GlobalParamId.FxType), 0, FxTypes.Count - 1)];
            ApplyFxLabels();
            Bpm = (decimal)_params.Get(GlobalParamId.Bpm);
            FxPre = _params.Get(GlobalParamId.FxRouting) >= 0.5f;
            RndSeqOn = _params.Get(GlobalParamId.RndSeq) >= 0.5f;
            TransposeValue = (int)_params.Get(GlobalParamId.Transpose);
            MuteSeq = _params.Get(GlobalParamId.MuteMode) >= 0.5f;
            TemperamentChoice.Load((int)_params.Get(GlobalParamId.Temperament));

            foreach (var st in Strips)
            {
                st.IsMuted = _params.GetBase(st.Index, ParamId.Mute) >= 0.5f;
                foreach (var k in st.Knobs) k.Load(_params.GetBase(st.Index, Enum.Parse<ParamId>(k.MidiTarget)));
            }
            SelectLayer(SelIdx);      // layer editor, LFOs, voice, tuning key, steps (nested _loading is preserved)
        }
        finally { _loading = wasLoading; }
        RefreshPalettePanel();
        RefreshSteps();
        _baselineValid = false;                  // the edit baseline of the live-edit dirty tracking is re-taken once the palette is applied
        if (Seq.CurrentBank >= 0 && (_settings.LastBank != Seq.CurrentBank || _settings.LastSlot != Seq.CurrentSlot))
        {
            _settings.LastBank = Seq.CurrentBank; _settings.LastSlot = Seq.CurrentSlot; MarkSettingsDirty();
        }
    }

    // ------------------------------------------------------------ layer parameter writes

    /// <summary>
    /// Single path of every layer knob / combo write: while a step button is held the value becomes a direct P-lock of that step;
    /// otherwise <see cref="Sequencer.SetLayerParam"/> (base value, or a P-lock while PRM REC is armed).
    /// </summary>
    private float ApplyLayerParam(int layer, ParamId id, float value)
    {
        int held = Volatile.Read(ref _heldStep);
        if (held >= 0 && layer == Volatile.Read(ref _heldStepLayer) && Seq.SetLock(layer, held, id, value))
        {
            Volatile.Write(ref _stepsDirty, 1);
            return ParamTable.Clamp(id, value);
        }
        if (PrmRecOn) Volatile.Write(ref _stepsDirty, 1);
        return Seq.SetLayerParam(layer, id, value);
    }

    // ------------------------------------------------------------ transport

    [RelayCommand]
    private void Play()
    {
        if (Seq.Chain.Active) Seq.ClearChain();       // normal PLAY plays the current pattern, not the song
        Seq.Play();
    }

    /// <summary>STOP; <paramref name="natural"/> (Shift+click or the NAT toggle) lets sounding notes ring out.</summary>
    public void StopTransport(bool natural)
    {
        if (natural || NaturalStop) Seq.StopNatural(); else Seq.Stop();
        if (Seq.Chain.Active) Seq.ClearChain();
        ReleaseHeldStep();
    }

    [RelayCommand] private void StopPlain() => StopTransport(false);

    public void TogglePlayStop(bool natural = false)
    {
        if (Seq.IsPlaying) StopTransport(natural); else Play();
    }

    [RelayCommand] private void ToggleRec() => RecArmed = !RecArmed;
    [RelayCommand] private void TogglePrmRec() => PrmRecOn = !PrmRecOn;
    [RelayCommand] private void ToggleMetro() => MetroOn = !MetroOn;

    private void ExecuteTransportTarget(string target)
    {
        switch (target)
        {
            case MidiTargets.TransportPlay: Play(); break;
            case MidiTargets.TransportStop: StopTransport(false); break;
            case MidiTargets.TransportRecord: ToggleRec(); break;
            case MidiTargets.TransportPrmRec: TogglePrmRec(); break;
        }
    }

    /// <summary>Right-click "MIDI Learn" on a transport button.</summary>
    public void LearnTransport(string target) => StartLearn(target);
    public void ClearTransportMapping(string target) => ClearMapping(target);

    public string MappingText(string target)
    {
        var b = _map.Find(target);
        return b == null ? "not mapped" : $"CC {b.Cc} ({b.Source})";
    }

    // ------------------------------------------------------------ tick

    private void TickSequencer()
    {
        var seq = Seq;
        seq.Poll();
        _tickCount++;

        if (_pendingRefresh && (seq.PaletteApplied || Environment.TickCount64 - _pendingSince > 1500)) RefreshAll();
        if (_settingsDirtyTicks > 0 && --_settingsDirtyTicks == 0) SaveSettingsNow();
        TickProject();

        var state = seq.State;
        IsPlaying = state != TransportState.Stopped;
        RecModeText = !RecArmed ? "" : state == TransportState.Playing ? "REC · realtime"
            : state == TransportState.PreCount ? $"pre-count {seq.PreCountLeft}" : "STEP REC";
        CycleText = state == TransportState.Playing ? $"CYC {seq.CycleCount + 1}" : "CYC –";
        QueuedBlinkOn = (_tickCount / 8) % 2 == 0;

        RefreshPalettePanel();

        int ph = state == TransportState.Stopped ? -1 : seq.CurrentStep(SelIdx);
        if (ph != _lastPlayhead)
        {
            _lastPlayhead = ph;
            if (FollowPlayhead && ph >= 0 && ph / StepGrid.StepsPerPage != CurrentPage && StepGrid.IsPageEnabled(ph / StepGrid.StepsPerPage, LengthNow))
                CurrentPage = ph / StepGrid.StepsPerPage;
            UpdatePlayheadCells();
        }

        bool recording = RecArmed && state == TransportState.Playing;
        if (Interlocked.Exchange(ref _stepsDirty, 0) == 1 || (recording && _tickCount % 4 == 0)) RefreshSteps();
        if (state != TransportState.Stopped || seq.Chain.Active) RefreshChainStatus(); else if (_tickCount % 15 == 0) RefreshChainStatus();
    }

    private int LengthNow => Math.Clamp((int)_params.GetBase(SelIdx, ParamId.SeqLength), 1, SeqLimits.MaxSteps);

    // ------------------------------------------------------------ steps

    private void OnLayerSelectedForSteps(int layer)
    {
        SelectedStep = Math.Min(Seq.StepCursor(layer), LengthNow - 1);
        CurrentPage = SelectedStep / StepGrid.StepsPerPage;
        _tieStart = -1;
        _lastPlayhead = -2;
        RefreshSteps();
    }

    private void SelectPage(int page)
    {
        if (StepGrid.IsPageEnabled(page, LengthNow)) CurrentPage = page;
    }

    /// <summary>Re-reads the selected layer's track from the model and updates the grid, page buttons and step details.</summary>
    public void RefreshSteps()
    {
        var seq = Seq;
        int layer = SelIdx;
        var snap = seq.GetTrack(layer);
        _lastTrack = snap;
        int len = snap.Length;
        if (CurrentPage > StepGrid.LastPage(len)) { CurrentPage = StepGrid.LastPage(len); return; }   // re-enters through OnCurrentPageChanged
        if (SelectedStep >= len) { SelectedStep = len - 1; seq.SetStepCursor(layer, SelectedStep); }

        var cells = StepGrid.Derive(snap, CurrentPage);
        for (int i = 0; i < StepCells.Count; i++)
        {
            StepCells[i].Apply(cells[i]);
            StepCells[i].IsSelected = cells[i].Index == SelectedStep && cells[i].InLength;
        }
        UpdatePlayheadCells();
        foreach (var pg in Pages)
        {
            pg.IsEnabled = StepGrid.IsPageEnabled(pg.Value, len);
            pg.IsSelected = pg.Value == CurrentPage;
        }
        RefreshStepDetails(snap);
    }

    private void UpdatePlayheadCells()
    {
        foreach (var c in StepCells) c.IsPlayhead = c.Index == _lastPlayhead && c.InLength;
    }

    partial void OnSelectedStepChanged(int value)
    {
        foreach (var c in StepCells) c.IsSelected = c.Index == value && c.InLength;
        if (_lastTrack != null) RefreshStepDetails(_lastTrack);
    }

    private void RefreshStepDetails(TrackSnapshot snap)
    {
        int idx = Math.Clamp(SelectedStep, 0, snap.Steps.Count - 1);
        var st = snap.Steps[idx];
        string sig = $"{SelIdx}:{idx}:{st.Probability}:" + string.Join(",", st.Notes.Select(n => $"{n.Note}/{n.Velocity}/{n.Tie}"))
                     + ":" + string.Join(",", st.Locks.Select(l => $"{(int)l.Id}={l.Value}"));
        SelectedStepTitle = $"STEP {idx + 1}";
        SelectedStepProb = st.Probability < 100 ? $"{st.Probability}%" : "100%";
        if (sig == _detailSignature) return;
        _detailSignature = sig;
        StepNotes.Clear();
        foreach (var n in st.Notes.OrderBy(n => n.Note)) StepNotes.Add(new StepNoteRowViewModel(n.Note, n.Velocity, n.Tie, DeleteStepNote));
        StepLocks.Clear();
        foreach (var l in st.Locks) StepLocks.Add(new StepLockRowViewModel(l.Id, l.Value, DeleteStepLock));
    }

    private void DeleteStepNote(int note)
    {
        Seq.SetStepNote(SelIdx, SelectedStep, note);      // toggles: the note is on, so it is removed
        RefreshSteps();
    }

    private void DeleteStepLock(ParamId id)
    {
        Seq.ClearLock(SelIdx, SelectedStep, id);
        RefreshSteps();
    }

    /// <summary>Select a step (the step cursor of step recording).</summary>
    public void SelectStep(int index)
    {
        index = Math.Clamp(index, 0, LengthNow - 1);
        Seq.SetStepCursor(SelIdx, index);
        SelectedStep = index;
        int page = index / StepGrid.StepsPerPage;
        if (page != CurrentPage) CurrentPage = page;
    }

    public void MoveStepCursor(int delta)
    {
        int len = LengthNow;
        SelectStep(((SelectedStep + delta) % len + len) % len);
    }

    /// <summary>A step button went down: select the step, hold it (direct p-lock / direct note recording) or handle TIE input.</summary>
    public void StepPressed(int index)
    {
        if (index < 0 || index >= LengthNow) return;
        if (TieMode)
        {
            if (_tieStart < 0)
            {
                _tieStart = index;
                SelectStep(index);
                TieHint = $"TIE from step {index + 1}: hold a key, then click the end step";
            }
            else
            {
                CompleteTie(index);
            }
            return;
        }
        SelectStep(index);
        Volatile.Write(ref _heldStepLayer, SelIdx);
        Volatile.Write(ref _heldStep, index);
        Seq.HoldStep(SelIdx, index);
        ShowHeldStepLocks(index);
    }

    public void StepReleased() => ReleaseHeldStep();

    private void ReleaseHeldStep()
    {
        if (Volatile.Read(ref _heldStep) < 0) return;
        Seq.ReleaseStep(Volatile.Read(ref _heldStepLayer));
        Volatile.Write(ref _heldStep, -1);
        ReloadKnobsFromBase();
        RefreshSteps();
    }

    private void CompleteTie(int end)
    {
        int start = _tieStart;
        List<(int Note, int Velocity)> held;
        lock (_noteLock) held = _heldNotes.ToList();
        if (held.Count == 0) { TieHint = "TIE: hold a key while clicking the end step"; return; }
        if (end < start) { TieHint = "TIE: the end step must come after the start step"; return; }
        foreach (var (note, vel) in held)
            Seq.SetTie(SelIdx, start, end, StoredNote(note), vel > 0 ? vel : (int)_params.GetBase(SelIdx, ParamId.Velocity));
        _tieStart = -1;
        TieHint = $"TIE set: steps {start + 1}–{end + 1}";
        RefreshSteps();
    }

    /// <summary>Shows the locked values of the held step on the knobs (a step lock is what a knob turn edits while held).</summary>
    private void ShowHeldStepLocks(int step)
    {
        var st = Seq.GetStep(SelIdx, step);
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            foreach (var l in st.Locks)
                foreach (var k in _selKnobs.Concat(Strips[SelIdx].Knobs))
                    if (k.MidiTarget == l.Id.ToString()) k.Load(l.Value);
        }
        finally { _loading = wasLoading; }
    }

    private void ReloadKnobsFromBase()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            int l = SelIdx;
            foreach (var k in _selKnobs.Concat(Strips[l].Knobs)) k.Load(_params.GetBase(l, Enum.Parse<ParamId>(k.MidiTarget)));
        }
        finally { _loading = wasLoading; }
    }

    // step menu actions
    public void StepClear(int step) { Seq.ClearStep(SelIdx, step); RefreshSteps(); }
    public void StepCopy(int step) => Seq.CopyStep(SelIdx, step);
    public void StepPaste(int step) { Seq.PasteStep(SelIdx, step); RefreshSteps(); }
    public void StepProbability(int step, int percent) { Seq.SetStepProbability(SelIdx, step, percent); RefreshSteps(); }
    public void StepClearLocks(int step) { Seq.ClearLocks(SelIdx, step); RefreshSteps(); }
    public void StepSoundLock(int step) { Seq.SetSoundLock(SelIdx, step); RefreshSteps(); }
    public void ClearSelectedStep() => StepClear(SelectedStep);

    [RelayCommand] private void Duplicate(string? factor)
    {
        if (!int.TryParse(factor, out int f)) return;
        int len = Seq.DuplicateTrack(SelIdx, f);
        bool was = _loading; _loading = true;
        try { SeqLengthValue = len; } finally { _loading = was; }
        RefreshSteps();
    }
    [RelayCommand] private void ClearNotes() { Seq.ClearTrackNotes(SelIdx); RefreshSteps(); }
    [RelayCommand] private void ClearAllLocks() { Seq.ClearLocks(SelIdx); RefreshSteps(); }

    // ------------------------------------------------------------ notes (step recording)

    private int StoredNote(int played) => Math.Clamp(played - (int)MathF.Round(_params.Get(GlobalParamId.Transpose)), 0, 127);

    private void OnRoutedNoteOn(int layer, int note, int velocity)
    {
        var seq = Seq;
        lock (_noteLock) _heldNotes.Add((note, velocity));
        if (TieMode) return;
        int vel = velocity > 0 ? velocity : (int)_params.GetBase(layer, ParamId.Velocity);
        if (Volatile.Read(ref _heldStep) >= 0 || !(RecArmed && seq.State == TransportState.Stopped))
        {
            seq.RecordNoteOn(layer, note, velocity);          // direct (step held) or realtime recording
            if (Volatile.Read(ref _heldStep) >= 0) Volatile.Write(ref _stepsDirty, 1);
            return;
        }
        // step recording: the key toggles the note on the step cursor
        seq.SetStepNote(layer, seq.StepCursor(layer), StoredNote(note), vel);
        _stepInputPending = true;
        Volatile.Write(ref _stepsDirty, 1);
    }

    private void OnRoutedNoteOff(int layer, int note)
    {
        var seq = Seq;
        bool none;
        lock (_noteLock)
        {
            int i = _heldNotes.FindIndex(h => h.Note == note);
            if (i >= 0) _heldNotes.RemoveAt(i);
            none = _heldNotes.Count == 0;
        }
        seq.RecordNoteOff(layer, note);
        if (none && _stepInputPending && AutoStep && !TieMode)
        {
            _stepInputPending = false;
            seq.StepRest(layer);                              // auto-step: advance once the chord is released
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (layer == SelIdx) { SelectedStep = seq.StepCursor(layer); CurrentPage = SelectedStep / StepGrid.StepsPerPage; }
            });
        }
        else if (none) _stepInputPending = false;
    }

    private sealed class NoteTap(MainViewModel vm) : INoteListener
    {
        public void NoteOn(int layer, int note, int velocity) => vm.OnRoutedNoteOn(layer, note, velocity);
        public void NoteOff(int layer, int note) => vm.OnRoutedNoteOff(layer, note);
    }

    /// <summary>Window deactivated / closed: forget held keys and the held step.</summary>
    public void ReleaseSequencerHolds()
    {
        lock (_noteLock) _heldNotes.Clear();
        ReleaseHeldStep();
    }
}
