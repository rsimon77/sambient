using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sambient.App.ViewModels;

/// <summary>One entry of a list parameter (combo box / segmented button). <see cref="Value"/> is the engine value.</summary>
public sealed partial class ChoiceOption : ObservableObject
{
    public int Value { get; }
    public string Text { get; }
    /// <summary>False greys the entry out and makes it unselectable (e.g. UNI on the NOISE layer).</summary>
    [ObservableProperty] private bool _isEnabled = true;

    public ChoiceOption(int value, string text) { Value = value; Text = text; }
    public override string ToString() => Text;
}

/// <summary>
/// A list parameter of the selected layer (STRUCTURE, LFO SHAPE, VOICE MODE ...). User selections are written through the
/// supplied callback; <see cref="Load"/> shows an engine value without writing it back (layer switch).
/// </summary>
public sealed partial class ParamChoiceViewModel : ObservableObject
{
    private readonly Action<int> _apply;
    private bool _loading;

    public ObservableCollection<ChoiceOption> Options { get; } = new();
    [ObservableProperty] private ChoiceOption? _selected;
    [ObservableProperty] private string _label;

    /// <summary>Raised after a USER selection has been written (not for <see cref="Load"/>).</summary>
    public event Action<ChoiceOption>? UserChanged;

    public ParamChoiceViewModel(string label, IEnumerable<ChoiceOption> options, Action<int> apply)
    {
        _label = label; _apply = apply;
        foreach (var o in options) Options.Add(o);
        _selected = Options.FirstOrDefault();
    }

    partial void OnSelectedChanged(ChoiceOption? value)
    {
        if (_loading || value == null) return;
        _apply(value.Value);
        UserChanged?.Invoke(value);
    }

    /// <summary>Replaces the option list (selection is restored by the following <see cref="Load"/>).</summary>
    public void SetOptions(IEnumerable<ChoiceOption> options)
    {
        _loading = true;
        try
        {
            Options.Clear();
            foreach (var o in options) Options.Add(o);
        }
        finally { _loading = false; }
    }

    /// <summary>Selects the option with the engine value; falls back to the first enabled option. Returns the selected value.</summary>
    public int Load(int value)
    {
        _loading = true;
        try
        {
            Selected = Options.FirstOrDefault(o => o.Value == value) ?? Options.FirstOrDefault();
            return Selected?.Value ?? value;
        }
        finally { _loading = false; }
    }

    public ChoiceOption? Find(int value) => Options.FirstOrDefault(o => o.Value == value);
}

/// <summary>One LFO panel (LFO1 or LFO2): SHAPE / ASSIGN / TRIG lists plus the RATE and DEPTH knobs.</summary>
public sealed class LfoPanelViewModel
{
    public string Title { get; }
    public ParamChoiceViewModel Shape { get; }
    public ParamChoiceViewModel Assign { get; }
    public ParamChoiceViewModel Trig { get; }
    public ParamKnobViewModel RateKnob { get; }
    public ParamKnobViewModel DepthKnob { get; }

    public LfoPanelViewModel(string title, ParamChoiceViewModel shape, ParamChoiceViewModel assign, ParamChoiceViewModel trig,
        ParamKnobViewModel rate, ParamKnobViewModel depth)
    {
        Title = title; Shape = shape; Assign = assign; Trig = trig; RateKnob = rate; DepthKnob = depth;
    }
}
