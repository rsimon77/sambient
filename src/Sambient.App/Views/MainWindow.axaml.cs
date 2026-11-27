using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Windows.Input;
using Sambient.App.Controls;
using Sambient.App.ViewModels;

namespace Sambient.App.Views;

public partial class MainWindow : Window
{
    private ComputerKeyboard? _keys;
    private bool _closeConfirmed;
    private NativeMenuItem? _recentMenuItem;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel so that mapped letter keys are consumed before a focused ComboBox can use them for type-ahead.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => { ReleaseAllNotes(); Vm?.ReleaseSequencerHolds(); };
        Closing += OnWindowClosing;
        Opened += OnWindowOpened;

        DataContextChanged += (_, _) => AttachViewModel();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void AttachViewModel()
    {
        var vm = Vm;
        if (vm == null) return;

        // velocity 0 = the layer's fixed VELO parameter (computer keys have no velocity)
        _keys = new ComputerKeyboard(() => vm.KeyboardFirstNote, n => vm.PlayNote(n, 0), vm.StopNote, vm.ShiftKeyboardOctave);

        var piano = this.FindControl<PianoKeyboard>("Piano");
        if (piano != null)
        {
            piano.NoteOn += vm.PlayNote;
            piano.NoteOff += vm.StopNote;
        }

        // Shift+click on "Release holds" releases the holds of every layer (the Click event carries no modifiers).
        var release = this.FindControl<Button>("ReleaseHoldsButton");
        if (release != null)
        {
            bool shift = false;
            release.AddHandler(PointerPressedEvent, (_, e) => shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel);
            release.Click += (_, _) =>
            {
                if (shift) vm.ReleaseAllHolds(); else vm.ReleaseHoldsCommand.Execute(null);
                shift = false;
            };
        }

        vm.Dialogs = new AvaloniaProjectDialogs(this);
        BuildFileMenu(vm);
        vm.RecentChanged += () => RebuildRecentMenu(vm);
        RebuildRecentMenu(vm);
        var recent = this.FindControl<Button>("RecentButton");
        if (recent != null) recent.Click += (_, _) => ShowRecentFlyout(vm, recent);
        var nameBox = this.FindControl<TextBox>("ProjectNameBox");
        if (nameBox != null)
        {
            nameBox.LostFocus += (_, _) => vm.CommitProjectName();
            nameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { vm.CommitProjectName(); Focus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { vm.ProjectNameEdit = vm.ProjectName; Focus(); e.Handled = true; }
            };
        }

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.KeyboardVisible) && !vm.KeyboardVisible) ReleaseAllNotes();
        };
    }

    // ------------------------------------------------------------ File menu (system menu bar on macOS)

    private static KeyModifiers Cmd => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void BuildFileMenu(MainViewModel vm)
    {
        NativeMenuItem Item(string header, ICommand command, Key key, KeyModifiers extra = KeyModifiers.None)
            => new(header) { Command = command, Gesture = new KeyGesture(key, Cmd | extra) };

        _recentMenuItem = new NativeMenuItem("Open Recent") { Menu = new NativeMenu() };
        var file = new NativeMenu
        {
            Item("New", vm.NewProjectCommand, Key.N),
            Item("Open\u2026", vm.OpenProjectCommand, Key.O),
            _recentMenuItem,
            new NativeMenuItemSeparator(),
            Item("Save", vm.SaveProjectCommand, Key.S),
            Item("Save As\u2026", vm.SaveProjectAsCommand, Key.S, KeyModifiers.Shift),
            new NativeMenuItem("Revert to Saved") { Command = vm.RevertProjectCommand },
        };
        var root = new NativeMenu { new NativeMenuItem("File") { Menu = file } };
        NativeMenu.SetMenu(this, root);

        if (!OperatingSystem.IsMacOS())
        {
            // no system menu bar: the same shortcuts as window key bindings
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.N, Cmd), Command = vm.NewProjectCommand });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, Cmd), Command = vm.OpenProjectCommand });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, Cmd), Command = vm.SaveProjectCommand });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, Cmd | KeyModifiers.Shift), Command = vm.SaveProjectAsCommand });
        }
    }

    private void RebuildRecentMenu(MainViewModel vm)
    {
        if (_recentMenuItem?.Menu is not { } menu) return;
        menu.Items.Clear();
        if (vm.RecentFiles.Count == 0) { menu.Items.Add(new NativeMenuItem("No Recent Projects") { IsEnabled = false }); return; }
        foreach (var path in vm.RecentFiles)
        {
            var p = path;
            var item = new NativeMenuItem(Path.GetFileNameWithoutExtension(p));
            item.Click += (_, _) => _ = vm.OpenRecentAsync(p);
            menu.Items.Add(item);
        }
    }

    private void ShowRecentFlyout(MainViewModel vm, Avalonia.Controls.Control anchor)
    {
        var flyout = new MenuFlyout();
        if (vm.RecentFiles.Count == 0) flyout.Items.Add(new MenuItem { Header = "No recent projects", IsEnabled = false });
        foreach (var path in vm.RecentFiles)
        {
            var p = path;
            var mi = new MenuItem { Header = Path.GetFileNameWithoutExtension(p) };
            ToolTip.SetTip(mi, p);
            mi.Click += (_, _) => _ = vm.OpenRecentAsync(p);
            flyout.Items.Add(mi);
        }
        flyout.ShowAt(anchor);
    }

    // ------------------------------------------------------------ closing / startup

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        ReleaseAllNotes();
        var vm = Vm;
        if (_closeConfirmed || vm == null || !vm.HasUnsavedChanges) return;
        e.Cancel = true;                                   // ask first (Save / Don't Save / Cancel), then close again
        _ = ConfirmCloseAsync(vm);
    }

    private async Task ConfirmCloseAsync(MainViewModel vm)
    {
        bool ok;
        try { ok = await vm.ConfirmCloseAsync(); }
        catch (Exception) { ok = false; }
        if (!ok) return;
        _closeConfirmed = true;
        Close();
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        try { if (Vm is { } vm) await vm.RunStartupChecksAsync(); }
        catch (Exception) { /* a failing startup check must never take the app down */ }
    }

    private void ReleaseAllNotes()
    {
        this.FindControl<PianoKeyboard>("Piano")?.ReleaseAll();
        _keys?.ReleaseAll();
    }

    private bool TextInputFocused() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private readonly HashSet<Key> _shortcutsDown = new();

    /// <summary>
    /// Transport / sequencer shortcuts (none of these keys is a piano key): Space play/stop, Shift+Space natural stop, R REC,
    /// M metronome, N PRM REC, Left/Right step cursor, Delete/Backspace clear step, 1-4 layer. Ignored while a text box has focus.
    /// </summary>
    private bool HandleShortcut(KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return false;
        var mods = e.KeyModifiers & ~KeyModifiers.Shift;
        if (mods != KeyModifiers.None) return false;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Space: if (_shortcutsDown.Add(e.Key)) vm.TogglePlayStop(shift); return true;
            case Key.R when !shift: if (_shortcutsDown.Add(e.Key)) vm.ToggleRecCommand.Execute(null); return true;
            case Key.M when !shift: if (_shortcutsDown.Add(e.Key)) vm.ToggleMetroCommand.Execute(null); return true;
            case Key.N when !shift: if (_shortcutsDown.Add(e.Key)) vm.TogglePrmRecCommand.Execute(null); return true;
            case Key.Left: vm.MoveStepCursor(-1); return true;
            case Key.Right: vm.MoveStepCursor(1); return true;
            case Key.Delete or Key.Back: vm.ClearSelectedStep(); return true;
            case Key.D1 or Key.D2 or Key.D3 or Key.D4 when !shift:
                if (_shortcutsDown.Add(e.Key)) vm.SelectLayer(e.Key - Key.D1);
                return true;
        }
        return false;
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_keys == null || TextInputFocused()) return;
        if (HandleShortcut(e)) { e.Handled = true; return; }
        if (_keys.KeyDown(e)) e.Handled = true;
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (_shortcutsDown.Remove(e.Key)) { e.Handled = true; return; }
        if (_keys == null) return;
        if (_keys.KeyUp(e)) e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
