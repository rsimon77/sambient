using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Ctl = Avalonia.Controls.Control;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sambient.App.ViewModels;
using Sambient.Control;

namespace Sambient.App.Views;

public partial class TransportBar : UserControl
{
    public TransportBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;
    private bool _attached;

    private void Attach()
    {
        var vm = Vm;
        if (vm == null || _attached) return;
        _attached = true;

        // Shift+click on STOP = natural stop (the Click event carries no modifiers)
        var stop = this.FindControl<Button>("StopButton")!;
        bool shift = false;
        stop.AddHandler(PointerPressedEvent, (_, e) => shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel);
        stop.Click += (_, _) => { vm.StopTransport(shift); shift = false; };

        AttachLearnMenu(this.FindControl<Button>("PlayButton")!, MidiTargets.TransportPlay);
        AttachLearnMenu(stop, MidiTargets.TransportStop);
        AttachLearnMenu(this.FindControl<ToggleButton>("RecButton")!, MidiTargets.TransportRecord);
        AttachLearnMenu(this.FindControl<ToggleButton>("PrmRecButton")!, MidiTargets.TransportPrmRec);
        AttachMetroMenu(this.FindControl<ToggleButton>("MetroButton")!);
    }

    /// <summary>Right-click menu of a transport button: MIDI Learn / Clear MIDI mapping (CC bound while the Launchkey is connected).</summary>
    private void AttachLearnMenu(Ctl button, string target)
    {
        var info = new MenuItem { IsEnabled = false };
        var learn = new MenuItem { Header = "MIDI Learn" };
        learn.Click += (_, _) => Vm?.LearnTransport(target);
        var clear = new MenuItem { Header = "Clear MIDI mapping" };
        clear.Click += (_, _) => Vm?.ClearTransportMapping(target);
        var menu = new ContextMenu { ItemsSource = new Ctl[] { info, learn, clear } };
        menu.Opening += (_, _) => info.Header = "Mapping: " + (Vm?.MappingText(target) ?? "-");
        button.ContextMenu = menu;
    }

    private void AttachMetroMenu(Ctl button)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            var vm = Vm;
            if (vm == null) return;
            var vol = new MenuItem { Header = "Volume" };
            vol.ItemsSource = Enumerable.Range(0, 16).Select(v =>
            {
                var m = new MenuItem { Header = (vm.MetroVolume == v ? "✓ " : "    ") + v };
                m.Click += (_, _) => vm.MetroVolume = v;
                return m;
            }).ToArray();
            var pre = new MenuItem { Header = "Pre-count (realtime REC)" };
            pre.ItemsSource = Enumerable.Range(0, 9).Select(n =>
            {
                var m = new MenuItem { Header = (vm.PreCount == n ? "✓ " : "    ") + (n == 0 ? "OFF" : n + (n == 1 ? " beat" : " beats")) };
                m.Click += (_, _) => vm.PreCount = n;
                return m;
            }).ToArray();
            menu.ItemsSource = new Ctl[] { vol, pre };
        };
        button.ContextMenu = menu;
    }
}
