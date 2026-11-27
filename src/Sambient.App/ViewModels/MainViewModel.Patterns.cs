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
    [ObservableProperty] private string _chainStatusText = "No chain";
    [ObservableProperty] private bool _chainRunning;
    [ObservableProperty] private string _patternStatus = "";

    // collapsible sections (persisted in settings.json)
    [ObservableProperty] private bool _expandSeq = true;
    [ObservableProperty] private bool _expandPatterns = true;
    [ObservableProperty] private bool _expandChain;
    [ObservableProperty] private bool _expandTuning;
    [ObservableProperty] private bool _expandMidi;

    public decimal RepeatsMinimum => RelaxationOn ? 2 : 1;
    public decimal RepeatsMaximum => RelaxationOn ? 10 : 99;
    public string PlayChainText => ChainRunning ? "STOP CHAIN" : "PLAY CHAIN";

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
    }

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
            try { s.Name = _patternBank.GetName(b, s.Slot).TrimEnd(); s.IsUser = _patternBank.HasUserFile(b, s.Slot); }
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
        PatternStatus = $"Saved to {PatternBank.SlotId(BrowsedBank, s.Slot)}";
        RefreshSlotNames();
    }

    /// <summary>Overwrites the slot with an empty INIT palette (the user copy; banks 1-4 can revert to the factory preset).</summary>
    public void SlotInit(PatternSlotViewModel s)
    {
        _patternBank.Save(BrowsedBank, s.Slot, new PatternPalette("INIT"));
        if (Seq.CurrentBank == BrowsedBank && Seq.CurrentSlot == s.Slot) { Seq.LoadPaletteNow(BrowsedBank, s.Slot); AfterUiPaletteChange(); }
        RefreshSlotNames();
    }

    public bool CanRevert(PatternSlotViewModel s) => PatternBank.IsPresetBank(BrowsedBank) && _patternBank.HasUserFile(BrowsedBank, s.Slot);

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
        ChainItems.Add(new ChainItemViewModel(bank, slot, "", RemoveChain, MoveChain));
        RenumberChain();
    }

    private void RemoveChain(ChainItemViewModel item) { ChainItems.Remove(item); RenumberChain(); }

    private void MoveChain(ChainItemViewModel item, int delta)
    {
        int i = ChainItems.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= ChainItems.Count) return;
        ChainItems.Move(i, j);
        RenumberChain();
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
    }

    [RelayCommand]
    private void PlayOrStopChain()
    {
        if (Seq.Chain.Active) { StopTransport(false); return; }
        if (ChainItems.Count == 0) { ChainStatusText = "Chain is empty: turn CHAIN EDIT on and click slots"; return; }
        Seq.SetChain(ChainItems.Select(c => (c.Bank, c.Slot)).ToList(), ChainLoop, (int)(Repeats ?? 1), RelaxationOn, FadeSeconds);
        Seq.PlayChain();
        AfterUiPaletteChange();
    }

    private void RefreshChainStatus()
    {
        var c = Seq.Chain;
        ChainRunning = c.Active;
        int repeats = RelaxationOn ? (int)Math.Clamp(Repeats ?? 2, 2, 10) : (int)Math.Clamp(Repeats ?? 1, 1, 99);
        string text;
        if (!c.Active) text = ChainItems.Count == 0 ? "No chain" : $"Chain ready: {ChainItems.Count} palettes";
        else
        {
            text = $"Chain {c.Index + 1}/{c.Count} · cycle {Math.Min(c.CycleCount + 1, repeats)}/{repeats}";
            if (c.FadePhase == 1) text += $" · fading out {c.FadeGain:P0}";
            else if (c.FadePhase == 2) text += $" · fading in {c.FadeGain:P0}";
        }
        if (text != ChainStatusText) ChainStatusText = text;
    }
}
