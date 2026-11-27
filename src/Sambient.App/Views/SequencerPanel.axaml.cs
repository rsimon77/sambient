using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Ctl = Avalonia.Controls.Control;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sambient.App.ViewModels;

namespace Sambient.App.Views;

public partial class SequencerPanel : UserControl
{
    private static readonly int[] Probabilities = [25, 50, 75, 90, 100];

    public SequencerPanel()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
        AddHandler(PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Bubble);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private static Border? StepBorder(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is Border b && b.Classes.Contains("step")) return b;
        return null;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var vm = Vm;
        var border = StepBorder(e.Source);
        if (vm == null || border?.DataContext is not StepCellViewModel cell || !cell.InLength) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            vm.SelectStep(cell.Index);
            ShowStepMenu(border, cell.Index);
            e.Handled = true;
        }
        else if (props.IsLeftButtonPressed)
        {
            e.Pointer.Capture(border);          // the release is seen even outside the button
            vm.StepPressed(cell.Index);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (StepBorder(e.Source) != null) { Vm?.StepReleased(); }
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (StepBorder(e.Source) != null) Vm?.StepReleased();
    }

    private void ShowStepMenu(Ctl anchor, int step)
    {
        var vm = Vm!;
        MenuItem Item(string header, Action act)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => act();
            return m;
        }
        var prob = new MenuItem { Header = "Probability" };
        prob.ItemsSource = Probabilities.Select(p => (Ctl)Item(p + " %", () => vm.StepProbability(step, p))).ToArray();
        var menu = new ContextMenu
        {
            ItemsSource = new Ctl[]
            {
                Item("Clear step", () => vm.StepClear(step)),
                Item("Copy", () => vm.StepCopy(step)),
                Item("Paste", () => vm.StepPaste(step)),
                new Separator(),
                prob,
                Item("Clear locks", () => vm.StepClearLocks(step)),
                Item("Sound lock (WAVE, HARMONIC, balance, PITCH, detune, MOD)", () => vm.StepSoundLock(step)),
            },
        };
        menu.Open(anchor);
    }
}
