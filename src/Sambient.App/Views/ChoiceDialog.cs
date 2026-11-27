using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sambient.App.Views;

/// <summary>
/// A small modal dialog with a heading, a message and a row of buttons; closes with the index of the clicked button (Esc = the last
/// button). Used for "Save changes?", "Restore unsaved changes?", confirmations and error messages.
/// </summary>
public sealed class ChoiceDialog : Window
{
    private readonly int _cancelIndex;

    public ChoiceDialog(string title, string heading, string message, IReadOnlyList<string> buttons, int primaryIndex = 0, int cancelIndex = -1)
    {
        _cancelIndex = cancelIndex >= 0 ? cancelIndex : buttons.Count - 1;
        Title = title;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.Parse("#1e2128"));
        MinWidth = 420;
        MaxWidth = 520;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (int i = 0; i < buttons.Count; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == primaryIndex) { b.IsDefault = true; b.Background = new SolidColorBrush(Color.Parse("#b3261e")); b.Foreground = Brushes.White; }
            if (i == _cancelIndex) b.IsCancel = true;
            b.Click += (_, _) => Close(idx);
            row.Children.Add(b);
        }
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, Foreground = new SolidColorBrush(Color.Parse("#aab0bd")), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap },
                row,
            },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(_cancelIndex); e.Handled = true; } };
    }

    /// <summary>"Save changes to X before quitting?" with Save / Don't Save / Cancel (result 0 / 1 / 2).</summary>
    public static ChoiceDialog Unsaved(string projectName, string action)
        => new("Unsaved changes", $"Save changes to \"{projectName}\" before {action}?",
            "Your changes (palettes, song chain and settings) will be lost if you do not save them.", ["Save", "Don't Save", "Cancel"]);

    public static ChoiceDialog Restore(string projectName, DateTime savedUtc)
        => new("Restore unsaved changes", "Restore unsaved changes?",
            $"Sambient found an automatic backup of \"{projectName}\" from {savedUtc.ToLocalTime():g}, newer than the saved file. This usually means the app did not quit cleanly.",
            ["Restore", "Discard"], 0, 1);

    public static ChoiceDialog Confirm(string title, string message, string okText)
        => new(title, title, message, [okText, "Cancel"], 0, 1);

    public static ChoiceDialog Message(string title, string message) => new(title, title, message, ["OK"], 0, 0);

    public async Task<int> ShowAsync(Window? owner)
    {
        if (owner == null) { Show(); return -1; }
        return await ShowDialog<int>(owner);
    }
}
