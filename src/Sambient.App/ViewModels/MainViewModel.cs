using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sambient.App.Settings;
using Sambient.Audio;
using Sambient.Control;
using Sambient.Dsp.Params;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;

namespace Sambient.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int DefaultSampleRate = 48000;
    private const int DefaultFrames = 256;
    private const int Layer = 0;

    private readonly PortAudioOutput _audio = new();
    private readonly ParameterStore _params = new();
    private readonly WavetableBank _bank = new();
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
    public ObservableCollection<ParamKnobViewModel> LayerKnobs { get; } = new();
    public ParamKnobViewModel MasterKnob { get; }
    public IReadOnlyList<WaveOption> Waves { get; }
    public IReadOnlyList<FilterOption> Filters { get; } =
    [
        new(FilterType.Off, "Off"), new(FilterType.LowPass, "LPF"), new(FilterType.HighPass, "HPF"), new(FilterType.BandPass, "BPF"),
    ];

    /// <summary>UI-side note state shared by all note sources (drives the keyboard highlight).</summary>
    public HeldNoteTracker Notes { get; } = new();

    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;
    [ObservableProperty] private string _status = "Audio not started";
    [ObservableProperty] private WaveOption _selectedWave;
    [ObservableProperty] private FilterOption _selectedFilter;
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
        _map = MidiMap.LoadOrCreate();
        _learn = new MidiLearn(_map);

        _engine = new AudioEngine(DefaultSampleRate, _params, _bank);
        _callback.Engine = _engine;

        Waves = Enumerable.Range(1, WavetableBank.MaxWaves)
            .Select(i => WaveRecipes.TryGet(i, out var r) ? new WaveOption(i, r.ShortName, true) : new WaveOption(i, "", false))
            .ToArray();
        _selectedWave = Waves[(int)ParamTable.Get(ParamId.Wave).Default - 1];
        _selectedFilter = Filters[(int)ParamTable.Get(ParamId.FilterType).Default];

        AddKnob("HARMONIC", ParamId.Harmonic, v => $"{v:0}");
        AddKnob("CUTOFF", ParamId.Cutoff, v => FormatHz(ParamCurves.CutoffHz((float)v)));
        AddKnob("RESO", ParamId.Resonance, v => SelectedFilter.Type == FilterType.BandPass
            ? $"{ParamCurves.BandpassOctaves((float)v):0.0} oct" : $"Q {ParamCurves.ResonanceQ((float)v):0.0}");
        AddKnob("ATTACK", ParamId.Attack, v => FormatMs(ParamCurves.AttackMs((float)v)));
        AddKnob("RELEASE", ParamId.Release, v => FormatMs(ParamCurves.ReleaseMs((float)v)));
        AddKnob("LEVEL", ParamId.Level, v => FormatDb(ParamCurves.LevelGain((float)v)));
        AddKnob("PAN", ParamId.Pan, FormatPan, bipolar: true);
        AddKnob("VELO", ParamId.Velocity, v => $"{v:0}");

        var info = ParamTable.Get(GlobalParamId.MasterVolume);
        MasterKnob = new ParamKnobViewModel("VOL", nameof(GlobalParamId.MasterVolume), info.Min, info.Max, info.Default, false,
            v => FormatDb(ParamCurves.LevelGain((float)v)), v => Engine.SetGlobal(GlobalParamId.MasterVolume, (float)v));
        WireMidiCommands(MasterKnob);
        _knobsByTarget[MasterKnob.MidiTarget] = MasterKnob;

        _bank.EnsureBuiltAsync(WavetableBank.FallbackIndex);
        _bank.EnsureBuiltAsync(SelectedWave.Index);

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

    private void AddKnob(string label, ParamId id, Func<double, string> format, bool bipolar = false)
    {
        var info = ParamTable.Get(id);
        var k = new ParamKnobViewModel(label, id.ToString(), info.Min, info.Max, info.Default, bipolar, format,
            v => Engine.SetParam(Layer, id, (float)v));
        WireMidiCommands(k);
        LayerKnobs.Add(k);
        _knobsByTarget[k.MidiTarget] = k;
    }

    private void WireMidiCommands(ParamKnobViewModel k)
    {
        k.LearnCommand = new RelayCommand<string>(StartLearn);
        k.ClearMappingCommand = new RelayCommand<string>(ClearMapping);
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

    partial void OnSelectedWaveChanged(WaveOption value)
    {
        if (value is { IsAvailable: true }) Engine.SetParam(Layer, ParamId.Wave, value.Index);   // also requests the build
    }

    partial void OnSelectedFilterChanged(FilterOption value)
    {
        Engine.SetParam(Layer, ParamId.FilterType, (float)value.Type);
        _knobsByTarget.GetValueOrDefault("Resonance")?.RefreshDisplay();
    }

    partial void OnOctaveChanged(int value)
    {
        Engine.SetParam(Layer, ParamId.Octave, value);
        OnPropertyChanged(nameof(OctaveText));
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

    /// <summary>velocity 0 = use the layer's fixed VELO parameter.</summary>
    public void PlayNote(int note, int velocity)
    {
        Engine.NoteOn(Layer, note, velocity);
        Notes.NoteOn(note);
    }

    public void StopNote(int note)
    {
        Engine.NoteOff(Layer, note);
        Notes.NoteOff(note);
    }

    public void AllNotesOff()
    {
        Engine.AllNotesOff();
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
                var engine = new AudioEngine(rate, _params, _bank);
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
        foreach (var k in _knobsByTarget.Values) k.ApplyPendingMidi();

        var e = Engine;
        VoicesText = $"Voices {e.ActiveVoices}" + (e.DroppedEvents > 0 ? $"  (dropped events {e.DroppedEvents})" : "");
        MeterL = ToMeter(e.PeakLeft);
        MeterR = ToMeter(e.PeakRight);
        LastMidiEvent = _lastMidi;

        int idx = SelectedWave.Index;
        bool ready = _bank.IsReady(idx);
        PreviewBuilding = !ready;
        var table = ready ? _bank.Get(idx) : (PreviewTable ?? _bank.Get(idx));
        if (!ReferenceEquals(table, PreviewTable)) PreviewTable = table;
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
        Engine.NoteOn(Layer, n.Note, n.Velocity);
        _lastMidi = $"Note on {n.Note} vel {n.Velocity}  (ch {n.Channel + 1})";
        Dispatcher.UIThread.Post(() => Notes.NoteOn(n.Note));
    }

    private void OnMidiNoteOff(ControllerNote n)
    {
        if (n.Source != ControllerSource.Keys) return;
        Engine.NoteOff(Layer, n.Note);
        _lastMidi = $"Note off {n.Note}  (ch {n.Channel + 1})";
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
                foreach (var k in _knobsByTarget.Values) k.IsLearning = false;
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

        if (_map.TryResolve(cc.Source, cc.Channel, cc.Controller, out var target) && _knobsByTarget.TryGetValue(target, out var knob))
            knob.SetFromMidi(cc.Value / 127.0);
    }

    private void StartLearn(string? target)
    {
        if (target == null) return;
        foreach (var k in _knobsByTarget.Values) k.IsLearning = false;
        _learn.Arm(target);
        if (_knobsByTarget.TryGetValue(target, out var knob)) knob.IsLearning = true;
        MidiLearnStatus = $"MIDI learn: move a knob on your controller to bind {target}…";
    }

    private void ClearMapping(string? target)
    {
        if (target == null) return;
        if (_learn.ArmedTarget == target) _learn.Cancel();
        if (_knobsByTarget.TryGetValue(target, out var knob)) knob.IsLearning = false;
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
