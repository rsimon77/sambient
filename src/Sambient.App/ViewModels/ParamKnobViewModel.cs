using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sambient.App.ViewModels;

/// <summary>
/// One knob bound to one engine parameter. UI edits write the engine immediately (lock-free store). MIDI CCs call
/// <see cref="SetFromMidi"/> on the MIDI thread: the engine is written directly and the UI value is picked up by the
/// 30 Hz timer (<see cref="ApplyPendingMidi"/>), so no dispatcher post per CC message.
/// </summary>
public sealed partial class ParamKnobViewModel : ObservableObject
{
    private readonly Func<double, string> _format;
    private readonly Action<double> _apply;
    private double _pending;
    private int _dirty;
    private bool _loading;

    /// <summary>Display label (may change at run time, e.g. FX SPEED -> TIME).</summary>
    [ObservableProperty] private string _label;
    /// <summary>Parameter id used by the MIDI map (enum name, e.g. "Cutoff").</summary>
    public string MidiTarget { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double DefaultValue { get; }
    public bool IsBipolar { get; }
    public ICommand? LearnCommand { get; set; }
    public ICommand? ClearMappingCommand { get; set; }

    [ObservableProperty] private double _value;
    [ObservableProperty] private string _displayText = "";
    [ObservableProperty] private bool _isLearning;
    /// <summary>False greys the knob out and ignores the mouse (e.g. BALANCE on ATMOS1).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Opacity))] private bool _isEnabled = true;
    public double Opacity => IsEnabled ? 1.0 : 0.35;

    public ParamKnobViewModel(string label, string midiTarget, double min, double max, double def, bool bipolar,
        Func<double, string> format, Action<double> apply, double? initial = null)
    {
        _label = label; MidiTarget = midiTarget; Minimum = min; Maximum = max; DefaultValue = def; IsBipolar = bipolar;
        _format = format; _apply = apply;
        _value = initial ?? def;
        _displayText = format(_value);
    }

    partial void OnValueChanged(double value)
    {
        if (!_loading) _apply(value);
        DisplayText = _format(value);
    }

    /// <summary>Shows <paramref name="value"/> without writing it to the engine (the engine already holds it, e.g. after a layer switch).</summary>
    public void Load(double value)
    {
        _loading = true;
        try { Value = Math.Clamp(value, Minimum, Maximum); }
        finally { _loading = false; }
        DisplayText = _format(Value);
    }

    public void RefreshDisplay() => DisplayText = _format(Value);

    /// <summary>Pushes the current value into the engine (e.g. after the engine was recreated).</summary>
    public void Reapply() => _apply(Value);

    /// <summary>MIDI thread: normalized 0..1.</summary>
    public void SetFromMidi(double normalized)
    {
        double v = Minimum + Math.Clamp(normalized, 0, 1) * (Maximum - Minimum);
        _apply(v);
        Volatile.Write(ref _pending, v);
        Volatile.Write(ref _dirty, 1);
    }

    /// <summary>UI thread (timer).</summary>
    public void ApplyPendingMidi()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1) Value = Volatile.Read(ref _pending);
    }
}

public sealed record WaveOption(int Index, string Name, bool IsAvailable)
{
    public string Text => IsAvailable ? $"{Index} {Name}" : $"{Index} (n/a)";
    public double Opacity => IsAvailable ? 1.0 : 0.4;
}

public sealed record FilterOption(Sambient.Engine.FilterType Type, string Text);

public sealed record PlayModeOption(Sambient.Dsp.Noise.SamplePlayMode Mode, string Text);

public sealed record ReverbOption(Sambient.Dsp.Effects.ReverbType Type, string Text);

public sealed record FxOption(Sambient.Dsp.Effects.MasterFxType Type, string Text, string SpeedLabel, string AmountLabel);
