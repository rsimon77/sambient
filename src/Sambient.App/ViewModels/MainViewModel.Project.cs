using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sambient.App.Projects;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.App.ViewModels;

/// <summary>
/// Projects: the open project (all palettes, song chain, project settings), the File commands, dirty tracking and autosave.
///
/// DIRTY RULES. The project is dirty after any change a file save would persist: SAVE / "Save here" of a palette into a slot, init /
/// revert / rename of a slot, song-chain edits, project settings (BPM mode, R.RST, mute mode, metronome, pre-count, master tune) and
/// the project name. Live edits of the CURRENT palette that were not SAVEd into its slot (knobs, steps, BPM ...) mark the project dirty
/// too (detected by comparing a content fingerprint of the palette twice a second), and Save / Save As / PLAY SONG first capture the
/// current palette into its slot - "save everything". An INIT palette that belongs to no slot has nothing to be captured into.
/// Selecting another palette still drops the unsaved edits of the previous one (use SAVE first), as on the hardware.
/// </summary>
public partial class MainViewModel
{
    private readonly ProjectSession _session = null!;
    private readonly StartupResult _startup = null!;
    private bool _applyingProject;                // true while project data is pushed into the UI/sequencer (no write-back)
    private int _projectChanged;                  // set from any thread, consumed by the UI timer
    private ulong _editBaseline;
    private bool _baselineValid;
    private IProjectDialogs? _dialogs;

    public ProjectSession Session => _session;

    /// <summary>The dialog implementation (set by the main window; tests inject a fake).</summary>
    public IProjectDialogs Dialogs { get => _dialogs ?? NoDialogs.Instance; set => _dialogs = value; }

    [ObservableProperty] private string _windowTitle = "Sambient";
    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private bool _projectDirty;
    [ObservableProperty] private string _projectPathText = "";
    /// <summary>Text box of the project bar: committed (renames the project) on Enter / focus loss.</summary>
    [ObservableProperty] private string _projectNameEdit = "";

    /// <summary>Raised on the UI thread when the Open Recent list changed (the native menu rebuilds its submenu).</summary>
    public event Action? RecentChanged;
    public IReadOnlyList<string> RecentFiles { get; private set; } = [];
    public StartupResult Startup => _startup;
    public bool HasUnsavedChanges { get { DetectLiveEdits(); return _session.IsDirty; } }

    // ------------------------------------------------------------ state

    private string _lastRecentSignature = "";

    /// <summary>Title, name, dirty flag, recent list (UI thread).</summary>
    public void RefreshProjectState()
    {
        var p = _session.Project;
        WindowTitle = _session.WindowTitle;
        ProjectName = p.Name;
        ProjectDirty = p.IsDirty;
        ProjectPathText = p.FilePath ?? "not saved yet";
        if (ProjectNameEdit != p.Name) ProjectNameEdit = p.Name;
        RevertProjectCommand.NotifyCanExecuteChanged();
        var recent = _session.Recent;
        string sig = string.Join("|", recent);
        if (sig != _lastRecentSignature)
        {
            _lastRecentSignature = sig;
            RecentFiles = recent;
            RecentChanged?.Invoke();
        }
        if (!p.IsDirty) _baselineValid = false;
    }

    private void TickProject()
    {
        if (Interlocked.Exchange(ref _projectChanged, 0) == 1) RefreshProjectState();
        if (_tickCount % 15 == 7) DetectLiveEdits();
        if (_tickCount % 30 == 11)
            _ = _session.TryAutosave(Environment.TickCount64, () => LiveOverlay());
    }

    private (int Index, PatternPalette Palette)? LiveOverlay()
    {
        int cb = Seq.CurrentBank;
        if (cb < 0 || _pendingRefresh || !Seq.PaletteApplied) return null;
        return (PatternBank.SlotIndex(cb, Seq.CurrentSlot), Seq.Capture());
    }

    /// <summary>Marks the project dirty when the current palette differs from its state when it was loaded / last saved.</summary>
    public void DetectLiveEdits()
    {
        if (_session.IsDirty || _pendingRefresh) return;
        var seq = Seq;
        if (!seq.PaletteApplied) return;
        ulong fp = PatternFingerprint.Of(seq.Capture());
        if (!_baselineValid) { _editBaseline = fp; _baselineValid = true; return; }
        if (fp != _editBaseline) _session.Project.MarkDirty();
    }

    /// <summary>Writes the live state of the current palette into its project slot (no file is written). False if there is nothing to capture into.</summary>
    internal bool CommitCurrentPalette()
    {
        var seq = Seq;
        int cb = seq.CurrentBank;
        if (cb < 0 || _pendingRefresh || !seq.PaletteApplied) return false;
        _patternBank.Save(cb, seq.CurrentSlot, seq.Capture());
        return true;
    }

    // ------------------------------------------------------------ project settings <-> sequencer

    /// <summary>Project settings edited in the UI are written to the project (dirty when changed).</summary>
    private void PersistProjectSettings()
    {
        if (_applyingProject || _session == null) return;
        _session.Project.SetSettings(new ProjectSettings
        {
            BpmMode = BpmGlobal ? BpmMode.Global : BpmMode.Pattern,
            ResetReverbOnChange = ResetReverb,
            MuteMode = MuteSeq ? MuteMode.Sequence : MuteMode.Sound,
            MetronomeVolume = MetroVolume,
            PreCount = PreCount,
            MasterTuneCents = _params.Get(GlobalParamId.MasterTuneCents),
        });
    }

    /// <summary>The project's settings pushed into the UI and the sequencer (project open / revert / new).</summary>
    private void ApplyProjectSettings()
    {
        var ps = _session.Project.Settings;
        bool was = _applyingProject;
        _applyingProject = true;
        try
        {
            BpmGlobal = ps.BpmMode == BpmMode.Global;
            ResetReverb = ps.ResetReverbOnChange;
            MetroVolume = ps.MetronomeVolume;
            PreCount = ps.PreCount;
            MuteSeq = ps.MuteMode == MuteMode.Sequence;
            Seq.MuteMode = ps.MuteMode;
            Engine.SetGlobal(GlobalParamId.MasterTuneCents, ps.MasterTuneCents);
            MasterTuneKnob.Load(ps.MasterTuneCents);
        }
        finally { _applyingProject = was; }
    }

    /// <summary>A different project became current (New / Open / Revert / Restore): stop, apply its settings, chain and a palette.</summary>
    private void OnProjectReplaced()
    {
        if (Seq.IsPlaying || Seq.Chain.Active) StopTransport(false);
        Seq.ClearChain();
        _applyingProject = true;
        try
        {
            ApplyProjectSettings();
            LoadChainFromProject();
        }
        finally { _applyingProject = false; }
        try { Seq.LoadPaletteNow(_settings.LastBank, _settings.LastSlot); }
        catch { Seq.LoadPaletteNow(0, 0); }
        AfterUiPaletteChange();
        RefreshSlotNames();
        _baselineValid = false;
        RefreshProjectState();
    }

    // ------------------------------------------------------------ commands

    /// <summary>"Save changes before ...?" - true when it is fine to go on (saved, or deliberately not saved).</summary>
    private async Task<bool> ConfirmDiscardAsync(string action)
    {
        if (!HasUnsavedChanges) return true;
        var choice = await Dialogs.AskUnsavedAsync(_session.Project.Name, action);
        if (choice == UnsavedChoice.Cancel) return false;
        if (choice == UnsavedChoice.Save) return await SaveCoreAsync();
        return true;
    }

    /// <summary>Window closing: true when the app may quit.</summary>
    public async Task<bool> ConfirmCloseAsync() => await ConfirmDiscardAsync("quitting");

    [RelayCommand]
    private async Task NewProject()
    {
        if (!await ConfirmDiscardAsync("creating a new project")) return;
        _session.New();
        PatternStatus = "New project";
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        if (!await ConfirmDiscardAsync("opening another project")) return;
        var path = await Dialogs.PickOpenPathAsync(DialogDirectory());
        if (path != null) await OpenPathAsync(path);
    }

    public async Task OpenRecentAsync(string path)
    {
        if (!await ConfirmDiscardAsync("opening another project")) return;
        await OpenPathAsync(path);
    }

    private async Task OpenPathAsync(string path)
    {
        var err = _session.Open(path);
        if (err != null) await Dialogs.ShowMessageAsync("Cannot open project", err);
        else PatternStatus = $"Opened {_session.Project.Name}";
        RefreshProjectState();
    }

    [RelayCommand] private async Task SaveProject() => await SaveCoreAsync();

    [RelayCommand] private async Task SaveProjectAs() => await SaveAsCoreAsync();

    private string DialogDirectory()
    {
        var f = _session.Project.FilePath;
        return f != null && Path.GetDirectoryName(f) is { Length: > 0 } d ? d : _session.Locations.ProjectsDir;
    }

    /// <summary>Save (Cmd+S): to the project's file, or Save As when it was never saved. False when cancelled or failed.</summary>
    public async Task<bool> SaveCoreAsync()
    {
        if (_session.Project.FilePath == null) return await SaveAsCoreAsync();
        var err = _session.Save();
        if (err != null) { await Dialogs.ShowMessageAsync("Cannot save project", err); return false; }
        PatternStatus = $"Saved {_session.Project.Name}";
        RefreshProjectState();
        return true;
    }

    public async Task<bool> SaveAsCoreAsync()
    {
        var path = await Dialogs.PickSavePathAsync(DialogDirectory(), _session.Project.Name + ProjectFile.Extension);
        if (path == null) return false;
        var err = _session.SaveAs(path);
        if (err != null) { await Dialogs.ShowMessageAsync("Cannot save project", err); return false; }
        PatternStatus = $"Saved {_session.Project.Name}";
        RefreshProjectState();
        return true;
    }

    private bool CanRevertProject() => _session.CanRevert;

    [RelayCommand(CanExecute = nameof(CanRevertProject))]
    private async Task RevertProject()
    {
        if (!await Dialogs.ConfirmAsync("Revert to Saved", $"Discard all changes to \"{_session.Project.Name}\" and reload the saved file?", "Revert")) return;
        var err = _session.Revert();
        if (err != null) await Dialogs.ShowMessageAsync("Cannot revert", err);
        else PatternStatus = "Reverted to the saved project";
        RefreshProjectState();
    }

    /// <summary>Enter / focus loss in the project name box.</summary>
    public void CommitProjectName()
    {
        if (ProjectNameEdit == _session.Project.Name) return;
        _session.Project.Rename(ProjectNameEdit);
        RefreshProjectState();
    }

    /// <summary>Startup follow-up (window opened): shows a startup problem and offers to restore an autosave after a crash.</summary>
    public async Task RunStartupChecksAsync()
    {
        if (_startup.Migrated) PatternStatus = $"Migrated {_startup.MigratedPalettes} saved palette(s) into {Path.GetFileName(_session.Locations.DefaultProjectPath)}";
        if (_startup.Message != null) await Dialogs.ShowMessageAsync("Project", _startup.Message);
        var rec = _session.FindRecovery();
        if (rec == null) return;
        if (await Dialogs.AskRestoreAsync(rec))
        {
            var err = _session.RestoreRecovery();
            if (err != null) await Dialogs.ShowMessageAsync("Cannot restore", err);
            else PatternStatus = "Restored unsaved changes";
        }
        else _session.DiscardRecovery();
        RefreshProjectState();
    }

    private sealed class NoDialogs : IProjectDialogs
    {
        public static readonly NoDialogs Instance = new();
        public Task<UnsavedChoice> AskUnsavedAsync(string projectName, string action) => Task.FromResult(UnsavedChoice.Cancel);
        public Task<string?> PickOpenPathAsync(string initialDirectory) => Task.FromResult<string?>(null);
        public Task<string?> PickSavePathAsync(string initialDirectory, string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<bool> AskRestoreAsync(AutosaveInfo info) => Task.FromResult(false);
        public Task<bool> ConfirmAsync(string title, string message, string okText) => Task.FromResult(false);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
