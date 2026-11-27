using CommunityToolkit.Mvvm.ComponentModel;
using Sambient.Engine;

namespace Sambient.App.ViewModels;

/// <summary>Tuning panel (temperament, layer key, master tune, tune all) and the FX routing toggle.</summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _showMasterHz;
    [ObservableProperty] private bool _tuningSpecial;
    [ObservableProperty] private bool _keyEnabled;
    [ObservableProperty] private bool _octaveEnabled = true;
    [ObservableProperty] private bool _fxPre;

    public string FxRoutingText => FxPre ? "PRE" : "POST";
    public string MasterTuneTip => TuningSpecial
        ? "MASTER TUNE is ignored by SLFG / CHKR / PLNT (the selected concert pitch is used as is)"
        : "Master tune ±75 cents. Right-click the knob for MIDI learn, double-click resets";
    public string OctaveTip => TuningSpecial ? "OCTAVE is ignored by SLFG / CHKR / PLNT" : "Layer octave";

    partial void OnShowMasterHzChanged(bool value)
    {
        _settings.ShowMasterTuneHz = value; MarkSettingsDirty();
        MasterTuneKnob.RefreshDisplay();
    }

    partial void OnFxPreChanged(bool value)
    {
        OnPropertyChanged(nameof(FxRoutingText));
        if (!_loading) Engine.SetGlobal(GlobalParamId.FxRouting, value ? 1f : 0f);
    }

    /// <summary>Key labels / enabled states for the current temperament and the selected layer's key (reads the store, writes nothing).</summary>
    private void RefreshTuningUi()
    {
        var t = (Temperament)Math.Clamp((int)_params.Get(GlobalParamId.Temperament), 0, TuningLabels.TemperamentNames.Length - 1);
        TuningSpecial = Tuning.IsSpecialTemperament(t);
        var labels = TuningLabels.KeyLabels(t);
        TuningKeyChoice.SetOptions(labels.Select((l, i) => new ChoiceOption(i, l)));
        TuningKeyChoice.Load(Math.Clamp((int)_params.GetBase(SelIdx, ParamId.TuningKey), 0, labels.Count - 1));
        KeyEnabled = TuningLabels.KeyEnabled(t);
        OctaveEnabled = !TuningSpecial;
        MasterTuneKnob.IsEnabled = !TuningSpecial;
        MasterTuneKnob.RefreshDisplay();
        OnPropertyChanged(nameof(MasterTuneTip));
        OnPropertyChanged(nameof(OctaveTip));
    }
}
