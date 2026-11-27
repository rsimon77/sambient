using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sambient.App.ViewModels;

/// <summary>One compact mixer strip: select button, mute, WAVE name and the LEVEL / PAN / REVERB / SHIMMER knobs of one layer.</summary>
public sealed partial class LayerStripViewModel : ObservableObject
{
    public int Index { get; }
    public string Title { get; }
    public ObservableCollection<ParamKnobViewModel> Knobs { get; } = new();
    public ICommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private string _waveText = "";

    private readonly Action<int, bool> _setMute;

    public LayerStripViewModel(int index, string title, bool muted, Action<int> select, Action<int, bool> setMute)
    {
        Index = index; Title = title; _isMuted = muted; _setMute = setMute;
        SelectCommand = new RelayCommand(() => select(index));
    }

    partial void OnIsMutedChanged(bool value) => _setMute(Index, value);

    /// <summary>The knob bound to the MIDI target id (e.g. "Level"), or null.</summary>
    public ParamKnobViewModel? FindKnob(string target)
    {
        foreach (var k in Knobs) if (k.MidiTarget == target) return k;
        return null;
    }
}
