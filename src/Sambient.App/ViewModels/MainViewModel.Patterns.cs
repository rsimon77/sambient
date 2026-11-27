using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.App.ViewModels;

/// <summary>Pattern browser (banks / slots), chain + relaxation panel, collapsible section states.</summary>
public partial class MainViewModel
{
    private (int Bank, int Slot)? _lastCurrent;

    public ObservableCollection<SelectorItemViewModel> BankTabs { get; } = new();
    public ObservableCollection<PatternSlotViewModel> Slots { get; } = new();
    public ObservableCollection<ChainItemViewModel> ChainItems { get; } = new();
    public IReadOnlyList<int> FadeOptions { get; } = [5, 10, 15, 20, 25, 30];

    [ObservableProperty] private int _browsedBank;
    [ObservableProperty] private bool _chainEdit;
    [ObservableProperty] private bool _chainLoop;
    [ObservableProperty] private bool _relaxationOn;
    [ObservableProperty] private decimal? _repeats = 1;
    [ObservableProperty] private int _fadeSeconds = 10;
    [ObservableProperty] private string _chainStatusText = "No song";
    [ObservableProperty] private bool _chainRunning;
    /// <summary>"3/8 · DEEPDRFT · cycle 2/4" while the song plays (transport bar), else empty.</summary>
    [ObservableProperty] private string _songPositionText = "";
    [ObservableProperty] private string _patternStatus = "";

    // collapsible sections (persisted in settings.json)
    [ObservableProperty] private bool _expandSeq = true;
    [ObservableProperty] private bool _expandPatterns = true;
    [ObservableProperty] private bool _expandChain;
    [ObservableProperty] private bool _expandTuning;
    [ObservableProperty] private bool _expandMidi;

    public decimal RepeatsMinimum => RelaxationOn ? 2 : 1;
    public decimal RepeatsMaximum => RelaxationOn ? 10 : 99;
    public string PlayChainText => ChainRunning ? "\u25A0 STOP SONG" : "\u25B6 PLAY SONG";

    private void SaveExpander(string key, bool value) { _settings.Expanders[key] = value; MarkSettingsDirty(); }
    partial void OnExpandSeqChanged(bool value) => SaveExpander("seq", value);
    partial void OnExpandPatternsChanged(bool value) => SaveExpander("patterns", value);
    partial void OnExpandChainChanged(bool value) => SaveExpander("chain", value);
    partial void OnExpandTuningChanged(bool value) => SaveExpander("tuning", value);
    partial void OnExpandMidiChanged(bool value) => SaveExpander("midi", value);

    partial void OnBrowsedBankChanged(int value)
    {
        foreach (var t in BankTabs) t.IsSelected = t.Value == value;
        RefreshSlotNames();
    }

    partial void OnRelaxationOnChanged(bool value)
    {
        OnPropertyChanged(nameof(RepeatsMinimum));
        OnPropertyChanged(nameof(RepeatsMaximum));
        Repeats = Math.Clamp(Repeats ?? 1, RepeatsMinimum, RepeatsMaximum);
        PersistChain();
    }

    partial void OnChainLoopChanged(bool value) => PersistChain();
    partial void OnRepeatsChanged(decimal? value) => PersistChain();
    partial void OnFadeSecondsChanged(int value) => PersistChain();

    partial void OnChainRunningChanged(bool value) => OnPropertyChanged(nameof(PlayChainText));

    private void BrowseBank(int bank) => BrowsedBank = bank;

    // ------------------------------------------------------------ browser

    /// <summary>Slot names / user-file flags of the browsed bank (reads the slots once; the bank caches the names).</summary>
    public void RefreshSlotNames()
    {
        int b = BrowsedBank;
        foreach (var s in Slots)
        {
            s.Bank = b;
            try { s.Name = _patternBank.GetName(b, s.Slot).TrimEnd(); s.IsUser = _patternBank.IsModified(b, s.Slot); }
            catch { s.Name = "?"; }
        }
        RefreshPalettePanel();
        RefreshChainItemNames();
    }

    /// <summary>Palette display, current / queued flags of the slot buttons and chain markers (cheap; every tick).</summary>
    private void RefreshPalettePanel()
    {
        var seq = Seq;
        int cb = seq.CurrentBank, cs = seq.CurrentSlot;
        PaletteText = cb < 0 ? $"-- {seq.CurrentName.TrimEnd()}" : $"{PatternBank.SlotId(cb, cs)} {seq.CurrentName.TrimEnd()}";
        var q = seq.QueuedSlot;
        QueuedText = q is { } qq ? $"→ {PatternBank.SlotId(qq.Bank, qq.Slot)} {SafeName(qq.Bank, qq.Slot)}" : "";
        if (_lastCurrent != (cb, cs))
        {
            _lastCurrent = (cb, cs);
            if (cb >= 0 && cb != BrowsedBank) BrowsedBank = cb;
        }
        foreach (var s in Slots)
        {
            s.IsCurrent = cb == BrowsedBank && cs == s.Slot;
            s.IsQueued = q is { } x && x.Bank == BrowsedBank && x.Slot == s.Slot;
            if (s.IsCurrent) s.Name = seq.CurrentName.TrimEnd();
            s.ChainText = ChainPositions(BrowsedBank, s.Slot);
        }
        PatternStatus = PaletteText;
    }

    private string SafeName(int bank, int slot)
    {
        try { return _patternBank.GetName(bank, slot).TrimEnd(); } catch { return ""; }
    }

    private string ChainPositions(int bank, int slot)
    {
        if (ChainItems.Count == 0) return "";
        var pos = new List<int>();
        foreach (var c in ChainItems) if (c.Bank == bank && c.Slot == slot) pos.Add(c.Position);
        return pos.Count == 0 ? "" : string.Join(",", pos);
    }

    private void OnSlotClicked(PatternSlotViewModel slot)
    {
        if (slot.IsRenaming) return;
        if (ChainEdit) { AppendChain(BrowsedBank, slot.Slot); return; }
        Seq.SelectPalette(BrowsedBank, slot.Slot);
        AfterUiPaletteChange();
    }

    /// <summary>A palette load requested from the UI: the model is current at once, the parameters follow when the audio thread applies it.</summary>
    private void AfterUiPaletteChange()
    {
        _pendingRefresh = true; _pendingSince = Environment.TickCount64;
        RefreshSteps();
        RefreshPalettePanel();
    }

    public void SlotLoadNow(PatternSlotViewModel s) { Seq.LoadPaletteNow(BrowsedBank, s.Slot); AfterUiPaletteChange(); }

    public void SlotSaveHere(PatternSlotViewModel s)
    {
        Seq.SaveAs(BrowsedBank, s.Slot);
        PatternStatus = $"Saved to {PatternBank.SlotId(BrowsedBank, s.Slot)} (project changed: Cmd+S to save the file)";
        RefreshSlotNames();
    }

    /// <summary>Overwrites the slot with an empty INIT palette in the project (banks 1-4 can revert to the factory preset).</summary>
    public void SlotInit(PatternSlotViewModel s)
    {
        _patternBank.Save(BrowsedBank, s.Slot, new PatternPalette("INIT"));
        if (Seq.CurrentBank == BrowsedBank && Seq.CurrentSlot == s.Slot) { Seq.LoadPaletteNow(BrowsedBank, s.Slot); AfterUiPaletteChange(); }
        RefreshSlotNames();
    }

    public bool CanRevert(PatternSlotViewModel s) => PatternBank.IsPresetBank(BrowsedBank) && _patternBank.IsModified(BrowsedBank, s.Slot);

    public void SlotRevert(PatternSlotViewModel s)
    {
        _patternBank.RevertToFactory(BrowsedBank, s.Slot);
        if (Seq.CurrentBank == BrowsedBank && Seq.CurrentSlot == s.Slot) { Seq.LoadPaletteNow(BrowsedBank, s.Slot); AfterUiPaletteChange(); }
        RefreshSlotNames();
    }

    public void SlotBeginRename(PatternSlotViewModel s)
    {
        foreach (var o in Slots) o.IsRenaming = false;
        s.EditName = s.Name;
        s.IsRenaming = true;
    }

    public void SlotCommitRename(PatternSlotViewModel s)
    {
        if (!s.IsRenaming) return;
        s.IsRenaming = false;
        var name = PatternPalette.NormalizeName(PatternNames.Sanitize(s.EditName));
        if (Seq.CurrentBank == BrowsedBank && Seq.CurrentSlot == s.Slot)
        {
            Seq.Rename(name);                     // the edited palette: SAVE keeps it
            PatternStatus = $"Renamed to {name.TrimEnd()} (SAVE to keep)";
        }
        else
        {
            var p = _patternBank.Load(BrowsedBank, s.Slot);
            p.Name = name;
            _patternBank.Save(BrowsedBank, s.Slot, p);
            PatternStatus = $"Renamed {PatternBank.SlotId(BrowsedBank, s.Slot)} to {name.TrimEnd()}";
        }
        RefreshSlotNames();
    }

    public void SlotCancelRename(PatternSlotViewModel s) => s.IsRenaming = false;

    [RelayCommand]
    private void SavePalette()
    {
        if (Seq.Save()) PatternStatus = $"Saved {Seq.CurrentName.TrimEnd()} to {PatternBank.SlotId(Seq.CurrentBank, Seq.CurrentSlot)}";
        else PatternStatus = "Unsaved palette: right-click a slot and choose \"Save here\"";
        RefreshSlotNames();
    }

    [RelayCommand]
    private void ReloadPalette()
    {
        Seq.Reload();
        AfterUiPaletteChange();
    }

    // ------------------------------------------------------------ chain / relaxation

    private void AppendChain(int bank, int slot)
    {
        ChainItems.Add(NewChainItem(bank, slot));
        RenumberChain();
        PersistChain();
    }

    private ChainItemViewModel NewChainItem(int bank, int slot) => new(bank, slot, "", RemoveChain, MoveChain, SelectChainItem);

    /// <summary>Click on an entry: PLAY SONG starts there (click it again to go back to "from the first entry").</summary>
    private void SelectChainItem(ChainItemViewModel item)
    {
        bool was = item.IsSelected;
        foreach (var c in ChainItems) c.IsSelected = false;
        item.IsSelected = !was;
    }

    private void RemoveChain(ChainItemViewModel item) { ChainItems.Remove(item); RenumberChain(); PersistChain(); }

    private void MoveChain(ChainItemViewModel item, int delta)
    {
        int i = ChainItems.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= ChainItems.Count) return;
        ChainItems.Move(i, j);
        RenumberChain();
        PersistChain();
    }

    private void RenumberChain()
    {
        for (int i = 0; i < ChainItems.Count; i++) ChainItems[i].Position = i + 1;
        RefreshChainItemNames();
        RefreshChainStatus();
    }

    private void RefreshChainItemNames()
    {
        var old = ChainItems.ToList();
        bool changed = false;
        foreach (var c in old)
        {
            string t = $"{c.Position}. {PatternBank.SlotId(c.Bank, c.Slot)} {SafeName(c.Bank, c.Slot)}";
            if (t != c.Text) { c.Text = t; changed = true; }
        }
        if (changed)
        {
            // items are plain objects: re-add to refresh the list view
            ChainItems.Clear();
            foreach (var c in old) ChainItems.Add(c);
        }
    }

    [RelayCommand]
    private void ClearChainItems()
    {
        ChainItems.Clear();
        if (Seq.Chain.Active) Seq.ClearChain();
        RefreshChainStatus();
        PersistChain();
    }

    /// <summary>The chain panel / transport state written back into the project's song chain (marks the project dirty when it changed).</summary>
    private void PersistChain()
    {
        if (_applyingProject || _session == null) return;
        var c = _session.Project.GetChain(0);
        c.Entries = ChainItems.Select(i => new ChainSlot(i.Bank, i.Slot)).ToList();
        c.Loop = ChainLoop;
        c.Relaxation = RelaxationOn;
        c.Repeats = (int)(Repeats ?? 1);
        c.FadeSeconds = FadeSeconds;
        _session.Project.SetChain(0, c);
    }

    /// <summary>Shows the project's song chain in the chain panel (project open / revert).</summary>
    private void LoadChainFromProject()
    {
        bool was = _applyingProject;
        _applyingProject = true;
        try
        {
            var c = _session.Project.GetChain(0);
            ChainLoop = c.Loop;
            RelaxationOn = c.Relaxation;
            Repeats = c.Repeats;
            FadeSeconds = c.FadeSeconds;
            ChainItems.Clear();
            foreach (var e in c.Entries) ChainItems.Add(NewChainItem(e.Bank, e.Slot));
            RenumberChain();
        }
        finally { _applyingProject = was; }
    }

    /// <summary>True while the song chain is playing (a finished, non-looping song is not "running").</summary>
    public bool SongPlaying => Seq.Chain.Active && Seq.IsPlaying;

    /// <summary>PLAY SONG: plays the project's song chain from the first entry (or the selected one), with its loop / repeats / relaxation settings.</summary>
    [RelayCommand]
    private void PlayOrStopSong()
    {
        if (SongPlaying) { StopTransport(false); return; }
        var c = _session.Project.GetChain(0);
        if (c.Entries.Count == 0) { ChainStatusText = "Song is empty: turn CHAIN EDIT on and click slots"; return; }
        CommitCurrentPalette();            // the song plays what you hear: live edits of the current palette are part of the project
        Seq.SetChain(c.Entries.Select(e => (e.Bank, e.Slot)).ToList(), c.Loop, c.Repeats, c.Relaxation, c.FadeSeconds, c.FadeInSeconds);
        int start = Math.Max(0, ChainItems.ToList().FindIndex(i => i.IsSelected));
        Seq.PlayChainFrom(start);
        AfterUiPaletteChange();
    }

    private void RefreshChainStatus()
    {
        var c = Seq.Chain;
        bool running = c.Active && Seq.IsPlaying;
        ChainRunning = running;
        int repeats = RelaxationOn ? (int)Math.Clamp(Repeats ?? 2, 2, 10) : (int)Math.Clamp(Repeats ?? 1, 1, 99);
        string text, pos = "";
        if (!c.Active) text = ChainItems.Count == 0 ? "No song" : $"Song ready: {ChainItems.Count} palettes";
        else
        {
            string name = c.Index >= 0 && c.Index < ChainItems.Count ? SafeName(ChainItems[c.Index].Bank, ChainItems[c.Index].Slot) : "";
            text = $"{c.Index + 1}/{c.Count} · {name} · cycle {Math.Min(c.CycleCount + 1, repeats)}/{repeats}";
            if (c.FadePhase == 1) text += $" · fading out {c.FadeGain:P0}";
            else if (c.FadePhase == 2) text += $" · fading in {c.FadeGain:P0}";
            if (running) pos = text;
            else text = "Song finished · " + text;
        }
        if (text != ChainStatusText) ChainStatusText = text;
        if (pos != SongPositionText) SongPositionText = pos;
    }
}
