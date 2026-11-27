using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sambient.App.Controls;
using Sambient.App.Settings;
using Sambient.Audio;
using Sambient.Control;
using Sambient.Dsp.Effects;
using Sambient.Dsp.Noise;
using Sambient.Dsp.Params;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;

namespace Sambient.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int DefaultSampleRate = 48000;
    private const int DefaultFrames = 256;

    private readonly PortAudioOutput _audio = new();
    private readonly ParameterStore _params = new();
    private readonly WavetableBank _bank = new();
    private readonly NoiseBank _noise = new();
    private readonly NoteRouter _router;
    private bool _loading;                       // true while the editor is being loaded from the parameter store
    private readonly EngineCallback _callback = new();
    private AudioEngine _engine;
    private readonly AppSettings _settings;
    private readonly MidiMap _map;
    private readonly MidiLearn _learn;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ParamKnobViewModel> _knobsByTarget = new();
    private bool _opening;
    private string _lastMidi = "";

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    public ObservableCollection<string> MidiPorts { get; } = new();
    /// <summary>Editor knobs of the SELECTED layer (HARMONIC, CUTOFF, RESO, ATTACK, RELEASE, VELO).</summary>
    public ObservableCollection<ParamKnobViewModel> LayerKnobs { get; } = new();
    public ObservableCollection<LayerStripViewModel> Strips { get; } = new();
    /// <summary>Every selected-layer knob (editor row, OSC, MOD, LFO, VOICE): reloaded from the store on layer switch.</summary>
    private readonly List<ParamKnobViewModel> _selKnobs = new();
    public ParamKnobViewModel PitchKnob { get; }
    public ParamKnobViewModel DetuneKnob { get; }
    public ParamKnobViewModel BalanceKnob { get; }
    public ParamKnobViewModel ModRateKnob { get; }
    public ParamKnobViewModel ModDepthKnob { get; }
    public ParamKnobViewModel GlideKnob { get; }
    public ParamKnobViewModel UnisonDetuneKnob { get; }
    public ParamChoiceViewModel StructureChoice { get; }
    public ParamChoiceViewModel ModShapeChoice { get; }
    public ParamChoiceViewModel VoiceModeChoice { get; }
    public ParamChoiceViewModel ArpTypeChoice { get; }
    public ParamChoiceViewModel StepNoteChoice { get; }
    public LfoPanelViewModel Lfo1 { get; }
    public LfoPanelViewModel Lfo2 { get; }
    /// <summary>UI mirror of the engine's HOLD latches (drawn on the on-screen keyboard).</summary>
    public LatchedNoteSet Latched { get; } = new(ParameterStore.MaxLayers);
    public ParamKnobViewModel MasterKnob { get; }
    public ParamKnobViewModel ReverbMixKnob { get; }
    public ParamKnobViewModel FxSpeedKnob { get; }
    public ParamKnobViewModel FxAmountKnob { get; }
    private readonly IReadOnlyList<WaveOption> _synthWaves;
    private readonly IReadOnlyList<WaveOption> _noiseWaves;
    public IReadOnlyList<PlayModeOption> PlayModes { get; } = [new(SamplePlayMode.Loop, "LOOP"), new(SamplePlayMode.OneShot, "SHOT")];
    public IReadOnlyList<ReverbOption> ReverbTypes { get; } =
    [
        new(ReverbType.Off, "OFF"), new(ReverbType.SmallL, "SMALL L"), new(ReverbType.SmallM, "SMALL M"), new(ReverbType.SmallH, "SMALL H"),
        new(ReverbType.LargeL, "LARGE L"), new(ReverbType.LargeM, "LARGE M"), new(ReverbType.LargeH, "LARGE H"),
        new(ReverbType.InfinityL, "INFINITY L"), new(ReverbType.InfinityM, "INFINITY M"), new(ReverbType.InfinityH, "INFINITY H"),
    ];
    public IReadOnlyList<FxOption> FxTypes { get; } =
    [
        new(MasterFxType.Off, "OFF", "SPEED", "AMOUNT"),
        new(MasterFxType.TapeDelay, "TAPE DLY", "TIME", "AMOUNT"),
        new(MasterFxType.ReverseDelay, "REV DLY", "TIME", "AMOUNT"),
        new(MasterFxType.Overdrive, "DRIVE", "GAIN", "MIX"),
        new(MasterFxType.Crush, "CRUSH", "BIT/RATE", "MIX"),
        new(MasterFxType.TiltEq, "TILT", "FREQ", "BALANCE"),
        new(MasterFxType.Chorus, "CHORUS", "RATE", "DEPTH/MIX"),
    ];
    public IReadOnlyList<FilterOption> Filters { get; } =
    [
        new(FilterType.Off, "Off"), new(FilterType.LowPass, "LPF"), new(FilterType.HighPass, "HPF"), new(FilterType.BandPass, "BPF"),
    ];

    /// <summary>UI-side note state shared by all note sources (drives the keyboard highlight).</summary>
    public HeldNoteTracker Notes { get; } = new();

    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;
    [ObservableProperty] private string _status = "Audio not started";
    [ObservableProperty] private IReadOnlyList<WaveOption> _waves;
    [ObservableProperty] private WaveOption? _selectedWave;
    [ObservableProperty] private FilterOption _selectedFilter;
    [ObservableProperty] private PlayModeOption _selectedPlayMode;
    [ObservableProperty] private ReverbOption _selectedReverb;
    [ObservableProperty] private FxOption _selectedFx;
    [ObservableProperty] private decimal? _bpm = 90;
    [ObservableProperty] private int _selectedLayerIndex;
    [ObservableProperty] private bool _midiChannelToLayer;
    [ObservableProperty] private NoiseSample? _previewSample;
    [ObservableProperty] private int _octave;
    [ObservableProperty] private bool _keyboardVisible;
    [ObservableProperty] private int _keyboardOctave;
    [ObservableProperty] private string _launchkeyStatus = "Launchkey: searching…";
    [ObservableProperty] private string _lastMidiEvent = "";
    [ObservableProperty] private string _midiLearnStatus = "";
    [ObservableProperty] private string _voicesText = "Voices 0";
    [ObservableProperty] private double _meterL;
    [ObservableProperty] private double _meterR;
    [ObservableProperty] private Wavetable? _previewTable;
    [ObservableProperty] private bool _previewBuilding = true;
    [ObservableProperty] private bool _holdMode;
    /// <summary>Structure of the selected layer (0..5), or -1 on the NOISE layer (no structure). Drives the Yin/Yang diagram.</summary>
    [ObservableProperty] private int _structureIndex = -1;
    [ObservableProperty] private string _modCaption = "";

    // structure of the selected synth layer (0..5, see Sambient.Engine.Structure); the labels/formats below depend on it
    private int _structure;
    private bool IsAtmos1 => !IsNoiseLayer && _structure == (int)Structure.Atmos1;
    private bool IsAtmos2 => !IsNoiseLayer && _structure == (int)Structure.Atmos2;
    public bool IsSynthLayer => !IsNoiseLayer;

    internal static readonly string[] StructureShortNames = ["DRN1", "DRN2", "PAD1", "PAD2", "ATM1", "ATM2"];
    private static readonly string[] AssignNames =
        ["OFF", "TUNE", "HARM", "BAL", "PTCH", "DT.FB", "L1.RT", "L1.DP", "MD.RT", "MD.DP", "FL.CO", "FL.RS", "PAN", "LVL", "\u2192SM", "\u2192RV", "O.LVL"];
    private static readonly string[] TrigNames = ["OFF", "1", "2", "3", "4", "5", "6", "7", "8", "INF"];
    private static readonly string[] VoiceModeNames = ["POLY", "MONO", "LGT", "UNI", "ARP"];
    private static readonly string[] ArpTypeNames = ["UP", "DOWN", "U.D", "D.U", "U.A.D", "D.A.U", "RNDM", "UP+1", "UP+2", "DN\u22121", "DN\u22122", "P.O"];
    private static readonly string[] StepNoteNames =
        ["8/1", "4/1", "3/1", "2/1", "1/1", "1/2", "1/.4", "1/4", "1/.8", "1/2T", "1/8", "1/.16", "1/4T", "1/16", "1/32"];

    public int VoiceModeValue => VoiceModeChoice.Selected?.Value ?? 0;
    public bool IsGlideVisible => VoiceModeValue is (int)VoiceMode.Mono or (int)VoiceMode.Legato;
    public bool IsUnisonVisible => VoiceModeValue == (int)VoiceMode.Unison;
    public bool IsArpVisible => VoiceModeValue == (int)VoiceMode.Arp;
    public bool IsVoiceAdjEmpty => !IsGlideVisible && !IsUnisonVisible && !IsArpVisible;

    public bool IsNoiseLayer => SelectedLayerIndex == ParameterStore.NoiseLayerIndex;
    public string LayerTitle => $"LAYER {SelectedLayerIndex + 1} · {DefaultPatch.LayerNames[SelectedLayerIndex]}";
    public string OctaveText => Octave == 0 ? "Octave 0" : $"Octave {Octave:+0;-0}";
    public string KeyboardRangeText => $"C{KeyboardOctave} – C{KeyboardOctave + 3}";
    public double PreviewHarmonic => Knob("Harmonic").Value;

    // --- MIDI / Launchkey ---
    private PortWatcher? _portWatcher;
    private ControllerInput? _controller;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _keyboardVisible = _settings.KeyboardVisible;
        _keyboardOctave = _settings.KeyboardOctave;
        _midiChannelToLayer = _settings.MidiChannelToLayer;
        _map = MidiMap.LoadOrCreate();
        _learn = new MidiLearn(_map);

        DefaultPatch.Apply(_params);          // startup sound; later replaced by the pattern-palette system
        _engine = new AudioEngine(DefaultSampleRate, _params, _bank, _noise);
        _callback.Engine = _engine;
        _router = new NoteRouter(() => Engine, _params) { MidiChannelToLayer = _midiChannelToLayer };

        _synthWaves = Enumerable.Range(1, WavetableBank.MaxWaves)
            .Select(i => WaveRecipes.TryGet(i, out var r) ? new WaveOption(i, r.ShortName, true) : new WaveOption(i, "", false))
            .ToArray();
        _noiseWaves = Enumerable.Range(1, NoiseBank.SlotCount)
            .Select(i => new WaveOption(i, NoiseBank.DefaultNames[i - 1], true))
            .ToArray();
        _waves = _synthWaves;
        _selectedFilter = Filters[0];
        _selectedPlayMode = PlayModes[0];
        _selectedReverb = ReverbTypes[(int)_params.Get(GlobalParamId.ReverbType)];
        _selectedFx = FxTypes[(int)_params.Get(GlobalParamId.FxType)];
        _bpm = (decimal)_params.Get(GlobalParamId.Bpm);

        // editor knobs address the SELECTED layer
        AddKnob("HARMONIC", ParamId.Harmonic, v => HarmonicText(v));
        AddKnob("CUTOFF", ParamId.Cutoff, v => FormatHz(ParamCurves.CutoffHz((float)v)));
        AddKnob("RESO", ParamId.Resonance, v => SelectedFilter.Type == FilterType.BandPass
            ? $"{ParamCurves.BandpassOctaves((float)v):0.0} oct" : $"Q {ParamCurves.ResonanceQ((float)v):0.0}");
        AddKnob("ATTACK", ParamId.Attack, v => FormatMs(ParamCurves.AttackMs((float)v)));
        AddKnob("RELEASE", ParamId.Release, v => FormatMs(ParamCurves.ReleaseMs((float)v)));
        AddKnob("VELO", ParamId.Velocity, v => $"{v:0}");

        // OSC / STRUCTURE
        PitchKnob = AddSelKnob("PITCH", ParamId.Pitch, PitchText, bipolar: true);
        DetuneKnob = AddSelKnob("DETUNE", ParamId.Detune, DetuneText, bipolar: true);
        BalanceKnob = AddSelKnob("BALANCE", ParamId.Balance, BalanceText, bipolar: true);
        ModRateKnob = AddSelKnob("MOD RATE", ParamId.ModRate, v => $"{v:0}");
        ModDepthKnob = AddSelKnob("MOD DEPTH", ParamId.ModDepth, v => $"{v:0}");
        StructureChoice = Choice("STRUCTURE", ParamId.Structure, StructureShortNames);
        StructureChoice.UserChanged += OnStructureChosen;
        ModShapeChoice = Choice("MOD SHAPE", ParamId.ModShape, LfoShapes.Names);

        // LFO1 / LFO2
        Lfo1 = CreateLfo(1, ParamId.Lfo1Shape, ParamId.Lfo1Assign, ParamId.Lfo1Trig, ParamId.Lfo1Rate, ParamId.Lfo1Depth);
        Lfo2 = CreateLfo(2, ParamId.Lfo2Shape, ParamId.Lfo2Assign, ParamId.Lfo2Trig, ParamId.Lfo2Rate, ParamId.Lfo2Depth);

        // VOICE
        VoiceModeChoice = Choice("MODE", ParamId.VoiceMode, VoiceModeNames);
        VoiceModeChoice.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParamChoiceViewModel.Selected)) RefreshVoiceUi(); };
        GlideKnob = AddSelKnob("GLIDE", ParamId.Glide, v => FormatMs(ParamCurves.GlideMs((float)v)));
        UnisonDetuneKnob = AddSelKnob("DETUNE", ParamId.UnisonDetune, v => $"{ParamCurves.UnisonDetuneCents((float)v):0.0} ct");
        ArpTypeChoice = Choice("ARP TYPE", ParamId.ArpType, ArpTypeNames);
        StepNoteChoice = Choice("NOTE", ParamId.StepNote, StepNoteNames);

        // mixer strips: LEVEL / PAN / ->REVERB / ->SHIMMER per layer
        for (int l = 0; l < ParameterStore.MaxLayers; l++)
        {
            int layer = l;
            var strip = new LayerStripViewModel(l, $"L{l + 1} {DefaultPatch.LayerNames[l]}", _params.Get(l, ParamId.Mute) >= 0.5f,
                SelectLayer, (li, m) => Engine.SetParam(li, ParamId.Mute, m ? 1 : 0));
            AddStripKnob(strip, "LEVEL", ParamId.Level, v => FormatDb(ParamCurves.LevelGain((float)v)));
            AddStripKnob(strip, "PAN", ParamId.Pan, FormatPan, bipolar: true);
            AddStripKnob(strip, "REVERB", ParamId.ReverbSend, v => $"{v:0}");
            AddStripKnob(strip, "SHIMMER", ParamId.ShimmerSend, v => $"{v:0}");
            Strips.Add(strip);
        }

        MasterKnob = AddGlobalKnob("VOL", GlobalParamId.MasterVolume, v => FormatDb(ParamCurves.LevelGain((float)v)));
        ReverbMixKnob = AddGlobalKnob("MIX", GlobalParamId.ReverbMix, v => $"{v:0}");
        FxSpeedKnob = AddGlobalKnob("SPEED", GlobalParamId.FxSpeed, v => $"{v:0}");
        FxAmountKnob = AddGlobalKnob("AMOUNT", GlobalParamId.FxAmount, v => $"{v:0}");
        ApplyFxLabels();

        _bank.EnsureBuiltAsync(WavetableBank.FallbackIndex);
        for (int l = 0; l < ParameterStore.NoiseLayerIndex; l++) _bank.EnsureBuiltAsync((int)_params.Get(l, ParamId.Wave));
        _noise.EnsureBuiltAsync((int)_params.Get(ParameterStore.NoiseLayerIndex, ParamId.Wave));

        SelectLayer((int)_params.Get(GlobalParamId.SelectedLayer) - 1);

        StartMidi();
        try
        {
            foreach (var d in _audio.GetOutputDevices()) Devices.Add(d);
            _opening = true;
            SelectedDevice = Devices.FirstOrDefault(d => d.IsDefault) ?? Devices.FirstOrDefault();
            _opening = false;
            OpenStream();
        }
        catch (Exception ex)
        {
            _opening = false;
            Status = "Audio error: " + ex.Message;
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => OnTick());
        _timer.Start();
    }

    private AudioEngine Engine => Volatile.Read(ref _engine);
    private ParamKnobViewModel Knob(string target) => _knobsByTarget[target];
    private int SelIdx => Math.Clamp(SelectedLayerIndex, 0, ParameterStore.MaxLayers - 1);

    /// <summary>All knobs (editor, global and every strip) for timer / learn housekeeping.</summary>
    private IEnumerable<ParamKnobViewModel> AllKnobs()
    {
        foreach (var k in _knobsByTarget.Values) yield return k;
        foreach (var st in Strips) foreach (var k in st.Knobs) yield return k;
    }

    /// <summary>
    /// MIDI target id -> knob. Global targets and editor targets are unique knobs; per-layer mixer targets (Level, Pan,
    /// ReverbSend, ShimmerSend) resolve to the knob of the SELECTED layer's strip, like the hardware.
    /// </summary>
    private ParamKnobViewModel? ResolveKnob(string target)
        => _knobsByTarget.TryGetValue(target, out var k) ? k : Strips[SelIdx].FindKnob(target);

    private void AddKnob(string label, ParamId id, Func<double, string> format, bool bipolar = false)
        => LayerKnobs.Add(AddSelKnob(label, id, format, bipolar));

    /// <summary>A knob for a parameter of the SELECTED layer (registered for MIDI and for reload on layer switch, but not in the editor row).</summary>
    private ParamKnobViewModel AddSelKnob(string label, ParamId id, Func<double, string> format, bool bipolar = false)
    {
        var info = ParamTable.Get(id);
        var k = new ParamKnobViewModel(label, id.ToString(), info.Min, info.Max, info.Default, bipolar, format,
            v => Engine.SetParam(SelIdx, id, (float)v), _params.Get(0, id));
        WireMidiCommands(k);
        _selKnobs.Add(k);
        _knobsByTarget[k.MidiTarget] = k;
        return k;
    }

    private ParamChoiceViewModel Choice(string label, ParamId id, IReadOnlyList<string> names)
        => new(label, names.Select((n, i) => new ChoiceOption(i, n)), v => Engine.SetParam(SelIdx, id, v));

    private LfoPanelViewModel CreateLfo(int n, ParamId shape, ParamId assign, ParamId trig, ParamId rate, ParamId depth)
    {
        var assignChoice = new ParamChoiceViewModel("ASSIGN", AssignOptions(n, false), v => Engine.SetParam(SelIdx, assign, v));
        return new LfoPanelViewModel($"LFO {n}", Choice("SHAPE", shape, LfoShapes.Names), assignChoice,
            Choice("TRIG", trig, TrigNames),
            AddSelKnob("RATE", rate, v => $"{v:0}"), AddSelKnob("DEPTH", depth, v => $"{v:0}"));
    }

    /// <summary>LFO destinations: L1.RT / L1.DP only exist for LFO2, O.LVL only for LFO1, BAL is hidden on the NOISE layer.</summary>
    private static IEnumerable<ChoiceOption> AssignOptions(int lfo, bool noise)
        => Enumerable.Range(0, AssignNames.Length)
            .Where(v => !(lfo == 1 && v is (int)LfoAssign.Lfo1Rate or (int)LfoAssign.Lfo1Depth)
                        && !(lfo == 2 && v == (int)LfoAssign.OscLevel)
                        && !(noise && v == (int)LfoAssign.Balance))
            .Select(v => new ChoiceOption(v, AssignNames[v]));

    // PITCH: 1/20 semitone units. ATMOS2 shows a modulator RATIO instead; ASSUMPTION (display only): the -240..240 range is
    // mapped linearly onto 0.50..15.99 as the hardware's displayed range, the engine uses its own mapping.
    private string PitchText(double v)
    {
        if (IsAtmos2) return "x" + (0.50 + (v + 240.0) / 480.0 * (15.99 - 0.50)).ToString("0.00", CultureInfo.InvariantCulture);
        return (v / 20.0).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " st";
    }

    private string DetuneText(double v)
    {
        if (IsAtmos2) return $"{v:0}";                       // FEEDBK 0..127
        int d = (int)Math.Clamp(Math.Round(v - 63.5, MidpointRounding.AwayFromZero), -63, 63);
        return d == 0 ? "0" : d.ToString("+0;-0", CultureInfo.InvariantCulture);
    }

    private string BalanceText(double v)
    {
        if (!BalanceKnobEnabled) return "\u2014";
        if (IsAtmos2) return $"{v:0}";                       // FM AMT 0..127
        int d = (int)Math.Clamp(Math.Round(v - 63.5, MidpointRounding.AwayFromZero), -63, 63);
        return d == 0 ? "CNTR" : d < 0 ? $"OSC1 {-d}" : $"OSC2 {d}";
    }

    private bool BalanceKnobEnabled => !IsNoiseLayer && !IsAtmos1;

    private void OnStructureChosen(ChoiceOption o)
    {
        _structure = o.Value;
        if (_structure is (int)Structure.Drone1 or (int)Structure.Drone2) Octave = -2;   // DRONE default (the engine sets it too)
        RefreshStructureUi();
    }

    /// <summary>Labels, enabled state and captions that depend on the selected layer and its structure.</summary>
    private void RefreshStructureUi()
    {
        bool noise = IsNoiseLayer;
        StructureIndex = noise ? -1 : _structure;
        PitchKnob.Label = IsAtmos2 ? "RATIO" : "PITCH";
        DetuneKnob.Label = IsAtmos2 ? "FEEDBK" : "DETUNE";
        BalanceKnob.Label = IsAtmos2 ? "FM AMT" : "BALANCE";
        BalanceKnob.IsEnabled = BalanceKnobEnabled;
        ModCaption = noise ? "MOD \u2192 PITCH" : (Structure)_structure switch
        {
            Structure.Drone1 => "MOD \u2192 DETUNE",
            Structure.Atmos1 => "MOD \u2192 RING",
            Structure.Atmos2 => "MOD \u2192 RATIO",
            _ => "MOD \u2192 HARMONIC",
        };
        OnPropertyChanged(nameof(IsSynthLayer));
        PitchKnob.RefreshDisplay(); DetuneKnob.RefreshDisplay(); BalanceKnob.RefreshDisplay();
    }

    private void RefreshVoiceUi()
    {
        OnPropertyChanged(nameof(VoiceModeValue));
        OnPropertyChanged(nameof(IsGlideVisible));
        OnPropertyChanged(nameof(IsUnisonVisible));
        OnPropertyChanged(nameof(IsArpVisible));
        OnPropertyChanged(nameof(IsVoiceAdjEmpty));
    }

    private void AddStripKnob(LayerStripViewModel strip, string label, ParamId id, Func<double, string> format, bool bipolar = false)
    {
        int layer = strip.Index;
        var info = ParamTable.Get(id);
        var k = new ParamKnobViewModel(label, id.ToString(), info.Min, info.Max, info.Default, bipolar, format,
            v => Engine.SetParam(layer, id, (float)v), _params.Get(layer, id));
        WireMidiCommands(k);
        strip.Knobs.Add(k);
    }

    private ParamKnobViewModel AddGlobalKnob(string label, GlobalParamId id, Func<double, string> format)
    {
        var info = ParamTable.Get(id);
        var k = new ParamKnobViewModel(label, id.ToString(), info.Min, info.Max, info.Default, false, format,
            v => Engine.SetGlobal(id, (float)v), _params.Get(id));
        WireMidiCommands(k);
        _knobsByTarget[k.MidiTarget] = k;
        return k;
    }

    private void WireMidiCommands(ParamKnobViewModel k)
    {
        k.LearnCommand = new RelayCommand<string>(StartLearn);
        k.ClearMappingCommand = new RelayCommand<string>(ClearMapping);
    }

    private string HarmonicText(double v)
    {
        if (!IsNoiseLayer) return $"{v:0}";
        int sample = (int)Math.Round(v / 127.0 * 100.0);
        return Math.Abs(v - 63.5) < 1.0 ? "CNTR" : $"N{100 - sample}/S{sample}";
    }

    // ------------------------------------------------------------ layer selection

    /// <summary>Selects a layer (0..3): the editor and the layer-parameter MIDI bindings now address it.</summary>
    public void SelectLayer(int index)
    {
        index = Math.Clamp(index, 0, ParameterStore.MaxLayers - 1);
        _loading = true;
        try
        {
            Engine.SetGlobal(GlobalParamId.SelectedLayer, index + 1);
            SelectedLayerIndex = index;
            foreach (var st in Strips) st.IsSelected = st.Index == index;
            OnPropertyChanged(nameof(IsNoiseLayer));
            OnPropertyChanged(nameof(LayerTitle));

            Knob("Harmonic").Label = IsNoiseLayer ? "NOISE↔SAMPLE" : "HARMONIC";
            Waves = IsNoiseLayer ? _noiseWaves : _synthWaves;
            int wave = (int)_params.Get(index, ParamId.Wave);
            SelectedWave = Waves[Math.Clamp(wave, 1, Waves.Count) - 1];
            SelectedFilter = Filters[(int)_params.Get(index, ParamId.FilterType)];
            Octave = (int)_params.Get(index, ParamId.Octave);
            SelectedPlayMode = PlayModes[(int)_params.Get(index, ParamId.NoisePlayMode) >= 1 ? 1 : 0];
            Latched.DisplayLayer = index;

            // list parameters + knobs of the new layer (the structure must be known before the knob texts are formatted)
            _structure = (int)_params.Get(index, ParamId.Structure);
            StructureChoice.Load(_structure);
            ModShapeChoice.Load((int)_params.Get(index, ParamId.ModShape));
            LoadLfo(Lfo1, 1, index, ParamId.Lfo1Shape, ParamId.Lfo1Assign, ParamId.Lfo1Trig);
            LoadLfo(Lfo2, 2, index, ParamId.Lfo2Shape, ParamId.Lfo2Assign, ParamId.Lfo2Trig);
            VoiceModeChoice.Options[(int)VoiceMode.Unison].IsEnabled = !IsNoiseLayer;
            int vmode = (int)_params.Get(index, ParamId.VoiceMode);
            if (IsNoiseLayer && vmode == (int)VoiceMode.Unison)
            {
                vmode = (int)VoiceMode.Poly;                  // UNISON does not exist on the NOISE layer
                Engine.SetParam(index, ParamId.VoiceMode, vmode);
            }
            VoiceModeChoice.Load(vmode);
            ArpTypeChoice.Load((int)_params.Get(index, ParamId.ArpType));
            StepNoteChoice.Load((int)_params.Get(index, ParamId.StepNote));
            RefreshVoiceUi();
            RefreshStructureUi();
            foreach (var k in _selKnobs) k.Load(_params.Get(index, Enum.Parse<ParamId>(k.MidiTarget)));
        }
        finally { _loading = false; }
    }

    private void LoadLfo(LfoPanelViewModel lfo, int n, int layer, ParamId shape, ParamId assign, ParamId trig)
    {
        lfo.Shape.Load((int)_params.Get(layer, shape));
        lfo.Assign.SetOptions(AssignOptions(n, IsNoiseLayer));
        int stored = (int)_params.Get(layer, assign);
        int shown = lfo.Assign.Load(stored);
        if (shown != stored) Engine.SetParam(layer, assign, shown);     // destination not available on this layer: back to OFF
        lfo.Trig.Load((int)_params.Get(layer, trig));
    }

    private void ApplyFxLabels()
    {
        FxSpeedKnob.Label = SelectedFx.SpeedLabel;
        FxAmountKnob.Label = SelectedFx.AmountLabel;
    }

    // ------------------------------------------------------------ formatting

    private static string FormatHz(float hz) => hz >= 1000f
        ? (hz / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " kHz"
        : hz.ToString("0", CultureInfo.InvariantCulture) + " Hz";
    private static string FormatMs(float ms) => ms >= 1000f ? $"{ms / 1000f:0.00} s" : $"{ms:0} ms";
    private static string FormatDb(float gain)
    {
        if (gain <= 0.00001f) return "-inf dB";
        float db = 20f * MathF.Log10(gain);
        return $"{(db >= 0 ? "+" : "")}{db:0.0} dB";
    }
    private static string FormatPan(double v) => Math.Abs(v) < 0.5 ? "C" : v < 0 ? $"L {-v:0}" : $"R {v:0}";

    // ------------------------------------------------------------ property hooks

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (!_opening && value != null) OpenStream();
    }

    partial void OnSelectedWaveChanged(WaveOption? value)
    {
        if (_loading || value is not { IsAvailable: true }) return;
        Engine.SetParam(SelIdx, ParamId.Wave, value.Index);   // also requests the build
    }

    partial void OnSelectedFilterChanged(FilterOption value)
    {
        if (!_loading) Engine.SetParam(SelIdx, ParamId.FilterType, (float)value.Type);
        _knobsByTarget.GetValueOrDefault("Resonance")?.RefreshDisplay();
    }

    partial void OnSelectedPlayModeChanged(PlayModeOption value)
    {
        if (!_loading && value != null) Engine.SetParam(SelIdx, ParamId.NoisePlayMode, (float)value.Mode);
    }

    partial void OnOctaveChanged(int value)
    {
        if (!_loading) Engine.SetParam(SelIdx, ParamId.Octave, value);
        OnPropertyChanged(nameof(OctaveText));
    }

    partial void OnSelectedReverbChanged(ReverbOption value)
    {
        if (value != null) Engine.SetGlobal(GlobalParamId.ReverbType, (float)value.Type);
    }

    partial void OnSelectedFxChanged(FxOption value)
    {
        if (value == null) return;
        Engine.SetGlobal(GlobalParamId.FxType, (float)value.Type);
        ApplyFxLabels();
    }

    partial void OnBpmChanged(decimal? value)
    {
        if (value is { } v) Engine.SetGlobal(GlobalParamId.Bpm, (float)v);
    }

    partial void OnMidiChannelToLayerChanged(bool value)
    {
        _router.MidiChannelToLayer = value;
        _settings.MidiChannelToLayer = value;
        _settings.Save();
    }

    partial void OnKeyboardVisibleChanged(bool value)
    {
        _settings.KeyboardVisible = value;
        _settings.Save();
    }

    partial void OnKeyboardOctaveChanged(int value)
    {
        _settings.KeyboardOctave = value;
        _settings.Save();
        OnPropertyChanged(nameof(KeyboardRangeText));
    }

    [RelayCommand] private void OctaveDown() => Octave = Math.Max(-3, Octave - 1);
    [RelayCommand] private void OctaveUp() => Octave = Math.Min(3, Octave + 1);
    [RelayCommand] private void KeyboardOctaveDown() => KeyboardOctave = Math.Max(1, KeyboardOctave - 1);
    [RelayCommand] private void KeyboardOctaveUp() => KeyboardOctave = Math.Min(4, KeyboardOctave + 1);

    public void ShiftKeyboardOctave(int delta) => KeyboardOctave = Math.Clamp(KeyboardOctave + delta, 1, 4);
    /// <summary>MIDI note of the C at the left edge of the on-screen keyboard (= the A key of the computer keyboard).</summary>
    public int KeyboardFirstNote => (KeyboardOctave + 1) * 12;

    // ------------------------------------------------------------ notes (UI thread sources: on-screen + computer keyboard)

    /// <summary>velocity 0 = use the layer's fixed VELO parameter. Goes to the selected layer.</summary>
    public void PlayNote(int note, int velocity)
    {
        if (HoldMode) { HoldNote(note, velocity); return; }
        _router.NoteOn(NoteSource.Ui, note, velocity);
        Notes.NoteOn(note);
    }

    /// <summary>Note-off goes to the layer that received the note-on, even if the selection changed meanwhile.</summary>
    public void StopNote(int note)
    {
        _router.NoteOff(NoteSource.Ui, note);
        Notes.NoteOff(note);
    }

    // ------------------------------------------------------------ HOLD (latched notes)

    /// <summary>HOLD mode: a key press toggles a latched note on the selected layer instead of playing momentarily.</summary>
    private void HoldNote(int note, int velocity)
    {
        int layer = SelIdx;
        int vel = velocity > 0 ? velocity : (int)_params.Get(layer, ParamId.Velocity);
        EngineHoldToggle(layer, note, vel);
        Latched.Toggle(layer, note);
    }

    [RelayCommand]
    private void ReleaseHolds()
    {
        EngineClearHolds(SelIdx);
        Latched.Clear(SelIdx);
    }

    /// <summary>Shift+click on "Release holds": every layer.</summary>
    public void ReleaseAllHolds()
    {
        EngineClearAllHolds();
        Latched.ClearAll();
    }

    // Adapter to the engine's HOLD API (HoldToggle / ClearHolds / ClearAllHolds).
    private void EngineHoldToggle(int layer, int note, int velocity) => Engine.HoldToggle(layer, note, velocity);
    private void EngineClearHolds(int layer) => Engine.ClearHolds(layer);
    private void EngineClearAllHolds() => Engine.ClearAllHolds();

    public void AllNotesOff()
    {
        Engine.AllNotesOff();
        _router.Clear();
        Notes.Clear();
    }

    // ------------------------------------------------------------ audio

    private void OpenStream()
    {
        var dev = SelectedDevice;
        if (dev == null) { Status = "No output device"; return; }

        try
        {
            _audio.Stop();
            int rate = DefaultSampleRate;
            try { _audio.Open(dev.Id, rate, DefaultFrames, _callback); }
            catch
            {
                // Device does not support 48 kHz: fall back to its native rate.
                rate = (int)dev.DefaultSampleRate;
                _audio.Open(dev.Id, rate, DefaultFrames, _callback);
            }

            if (rate != _engine.SampleRate)
            {
                // New engine at the actual sample rate; parameters and wavetables are shared, so settings carry over.
                var engine = new AudioEngine(rate, _params, _bank, _noise);
                Volatile.Write(ref _engine, engine);
                _callback.Engine = engine;
                Notes.Clear();
            }

            _audio.Start();
            Status = $"{_audio.SampleRate} Hz, {_audio.FramesPerBuffer} frames, latency {_audio.OutputLatencyMs:0.0} ms";
        }
        catch (Exception ex)
        {
            Status = "Audio error: " + ex.Message;
        }
    }

    private void OnTick()
    {
        foreach (var k in AllKnobs()) k.ApplyPendingMidi();
        foreach (var st in Strips)
        {
            int w = (int)_params.Get(st.Index, ParamId.Wave);
            if (st.Index == ParameterStore.NoiseLayerIndex)
                st.WaveText = NoiseBank.DefaultNames[Math.Clamp(w, 1, NoiseBank.SlotCount) - 1];
            else
            {
                string wave = WaveRecipes.TryGet(w, out var r) ? r.ShortName : $"W{w}";
                int s = Math.Clamp((int)_params.Get(st.Index, ParamId.Structure), 0, StructureShortNames.Length - 1);
                st.WaveText = $"{StructureShortNames[s]} \u00b7 {wave}";
            }
        }

        // the engine may change the octave itself (DRONE structures): follow the store without writing back
        int storeOctave = (int)_params.Get(SelIdx, ParamId.Octave);
        if (storeOctave != Octave)
        {
            _loading = true;
            try { Octave = storeOctave; }
            finally { _loading = false; }
        }

        var e = Engine;
        VoicesText = $"Voices {e.ActiveVoices}" + (e.DroppedEvents > 0 ? $"  (dropped events {e.DroppedEvents})" : "");
        MeterL = ToMeter(e.PeakLeft);
        MeterR = ToMeter(e.PeakRight);
        LastMidiEvent = _lastMidi;

        int idx = SelectedWave?.Index ?? 1;
        if (IsNoiseLayer)
        {
            bool nready = _noise.IsReady(idx);
            PreviewBuilding = !nready;
            var smp = nready ? _noise.Get(idx) : null;
            if (!ReferenceEquals(smp, PreviewSample)) PreviewSample = smp;
        }
        else
        {
            bool ready = _bank.IsReady(idx);
            PreviewBuilding = !ready;
            var table = ready ? _bank.Get(idx) : (PreviewTable ?? _bank.Get(idx));
            if (!ReferenceEquals(table, PreviewTable)) PreviewTable = table;
        }
        OnPropertyChanged(nameof(PreviewHarmonic));
    }

    /// <summary>-60 dBFS..0 dBFS -> 0..1.</summary>
    private static double ToMeter(float peak)
        => peak <= 0.001f ? 0 : Math.Clamp((20.0 * Math.Log10(peak) + 60.0) / 60.0, 0, 1);

    // ------------------------------------------------------------ MIDI

    private void StartMidi()
    {
        try
        {
            RefreshMidiPorts();
            _portWatcher = new PortWatcher();
            _portWatcher.LaunchkeyConnected += ports => Dispatcher.UIThread.Post(() => OnLaunchkeyConnected(ports));
            _portWatcher.LaunchkeyDisconnected += _ => Dispatcher.UIThread.Post(OnLaunchkeyDisconnected);
            _portWatcher.Start();
        }
        catch (Exception ex)
        {
            LaunchkeyStatus = "MIDI error: " + ex.Message;
        }
    }

    private void RefreshMidiPorts()
    {
        MidiPorts.Clear();
        foreach (var p in MidiPortCatalog.ListAll())
            MidiPorts.Add($"{(p.Direction == MidiPortDirection.In ? "IN " : "OUT")}  {p.Name}");
        if (MidiPorts.Count == 0) MidiPorts.Add("(no MIDI ports)");
    }

    private void OnLaunchkeyConnected(LaunchkeyPorts ports)
    {
        RefreshMidiPorts();
        _controller?.Dispose();
        _controller = null;
        try
        {
            var c = new ControllerInput(ports);
            // These handlers run on the MIDI driver thread. The engine queue and parameter store are lock-free, so the
            // engine is fed directly; only the UI-side state is marshalled.
            c.NoteOn += OnMidiNoteOn;
            c.NoteOff += OnMidiNoteOff;
            c.PitchBend += OnMidiPitchBend;
            c.ControlChange += OnMidiCc;
            _controller = c;
            string model = ports.Model is { } m ? $" {m}" : "";
            LaunchkeyStatus = $"Launchkey MK3{model}: connected" + (ports.IsComplete ? "" : " (partial: some ports missing)");
        }
        catch (Exception ex)
        {
            LaunchkeyStatus = "Launchkey found, but opening failed: " + ex.Message;
        }
    }

    private void OnLaunchkeyDisconnected()
    {
        _controller?.Dispose();
        _controller = null;
        RefreshMidiPorts();
        LaunchkeyStatus = "Launchkey: not connected";
        AllNotesOff();
        Engine.Sustain(false);
        Engine.PitchBend(0f);
        Notes.SetSustain(false);
    }

    private void OnMidiNoteOn(ControllerNote n)
    {
        if (n.Source != ControllerSource.Keys) return;
        int layer = _router.NoteOn(NoteSource.Midi, n.Note, n.Velocity, n.Channel);
        _lastMidi = $"Note on {n.Note} vel {n.Velocity}  (ch {n.Channel + 1} -> L{layer + 1})";
        Dispatcher.UIThread.Post(() => Notes.NoteOn(n.Note));
    }

    private void OnMidiNoteOff(ControllerNote n)
    {
        if (n.Source != ControllerSource.Keys) return;
        int layer = _router.NoteOff(NoteSource.Midi, n.Note);
        _lastMidi = $"Note off {n.Note}  (ch {n.Channel + 1}" + (layer >= 0 ? $" -> L{layer + 1})" : ")");
        Dispatcher.UIThread.Post(() => Notes.NoteOff(n.Note));
    }

    private void OnMidiPitchBend(ControllerPitchBend pb)
    {
        if (pb.Source != ControllerSource.Keys) return;
        Engine.PitchBend((pb.Value - 8192) / 8192f);
    }

    private void OnMidiCc(ControllerCc cc)
    {
        _lastMidi = $"CC {cc.Controller} = {cc.Value}  ({cc.Source}, ch {cc.Channel + 1})";

        var learned = _learn.TryCapture(cc);
        if (learned != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var k in AllKnobs()) k.IsLearning = false;
                MidiLearnStatus = $"Bound CC {cc.Controller} ({cc.Source}) to {learned}";
                _map.Save();
            });
            return;
        }

        if (cc.Source == ControllerSource.Keys)
        {
            if (cc.Controller == 1) { Engine.ModWheel(cc.Value / 127f); return; }
            if (cc.Controller == 64)
            {
                bool down = cc.Value >= 64;
                Engine.Sustain(down);
                Dispatcher.UIThread.Post(() => Notes.SetSustain(down));
                return;
            }
        }

        if (_map.TryResolve(cc.Source, cc.Channel, cc.Controller, out var target) && ResolveKnob(target) is { } knob)
            knob.SetFromMidi(cc.Value / 127.0);
    }

    private void StartLearn(string? target)
    {
        if (target == null) return;
        foreach (var k in AllKnobs()) k.IsLearning = false;
        _learn.Arm(target);
        if (ResolveKnob(target) is { } learnKnob) learnKnob.IsLearning = true;
        MidiLearnStatus = $"MIDI learn: move a knob on your controller to bind {target}…";
    }

    private void ClearMapping(string? target)
    {
        if (target == null) return;
        if (_learn.ArmedTarget == target) _learn.Cancel();
        foreach (var k in AllKnobs()) if (k.MidiTarget == target) k.IsLearning = false;
        _map.Clear(target);
        _map.Save();
        MidiLearnStatus = $"Cleared MIDI mapping of {target}";
    }

    public void Dispose()
    {
        _timer.Stop();
        _portWatcher?.Dispose();
        _controller?.Dispose();
        _audio.Dispose();
    }

    /// <summary>Adapts the engine to the audio callback; the engine reference is swapped on reopen.</summary>
    private sealed class EngineCallback : IAudioCallback
    {
        public AudioEngine? Engine;

        public void Render(Span<float> interleavedStereo, int frames)
        {
            var e = Volatile.Read(ref Engine);
            if (e == null) interleavedStereo.Clear();
            else e.Render(interleavedStereo, frames);
        }
    }
}
