using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sambient.App.Controls;
using Sambient.App.ViewModels;

namespace Sambient.App.Views;

public partial class MainWindow : Window
{
    private ComputerKeyboard? _keys;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel so that mapped letter keys are consumed before a focused ComboBox can use them for type-ahead.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => ReleaseAllNotes();
        Closing += (_, _) => ReleaseAllNotes();

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

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.KeyboardVisible) && !vm.KeyboardVisible) ReleaseAllNotes();
        };
    }

    private void ReleaseAllNotes()
    {
        this.FindControl<PianoKeyboard>("Piano")?.ReleaseAll();
        _keys?.ReleaseAll();
    }

    private bool TextInputFocused() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_keys == null || TextInputFocused()) return;
        if (_keys.KeyDown(e)) e.Handled = true;
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (_keys == null) return;
        if (_keys.KeyUp(e)) e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
