using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Ctl = Avalonia.Controls.Control;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sambient.App.ViewModels;

namespace Sambient.App.Views;

public partial class PatternBrowser : UserControl
{
    public PatternBrowser()
    {
        InitializeComponent();
        // tunnel: the right button must be seen before a Button handles the press
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    internal static T? FindVm<T>(object? source) where T : class
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is StyledElement se && se.DataContext is T t) return t;
        return null;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        var slot = FindVm<PatternSlotViewModel>(e.Source);
        var vm = Vm;
        if (slot == null || vm == null || e.Source is not Ctl src) return;
        e.Handled = true;

        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var m = new MenuItem { Header = header, IsEnabled = enabled };
            m.Click += (_, _) => act();
            return m;
        }
        var menu = new ContextMenu
        {
            ItemsSource = new Ctl[]
            {
                Item("Load now", () => vm.SlotLoadNow(slot)),
                Item("Save here", () => vm.SlotSaveHere(slot)),
                Item("Init slot (overwrite with empty palette)", () => vm.SlotInit(slot)),
                Item("Rename\u2026", () => BeginRename(slot)),
                Item("Revert to factory", () => vm.SlotRevert(slot), vm.CanRevert(slot)),
            },
        };
        menu.Open(src);
    }

    private void BeginRename(PatternSlotViewModel slot)
    {
        Vm?.SlotBeginRename(slot);
        Dispatcher.UIThread.Post(() =>
        {
            var tb = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => ReferenceEquals(t.DataContext, slot));
            if (tb == null) return;
            tb.Focus();
            tb.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox tb || tb.DataContext is not PatternSlotViewModel slot) return;
        if (e.Key == Key.Enter) { Vm?.SlotCommitRename(slot); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm?.SlotCancelRename(slot); e.Handled = true; }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox tb && tb.DataContext is PatternSlotViewModel slot && slot.IsRenaming) Vm?.SlotCommitRename(slot);
    }
}
