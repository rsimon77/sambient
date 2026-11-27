using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sambient.Audio;
using Sambient.Control;
using Sambient.Engine;

namespace Sambient.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int DefaultSampleRate = 48000;
    private const int DefaultFrames = 256;

    private readonly PortAudioOutput _audio = new();
    private TestToneEngine _engine = new(DefaultSampleRate);
    private readonly EngineCallback _callback = new();
    private bool _opening;

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

    /// <summary>Placeholder for the MIDI / Launchkey area; to be filled by Sambient.Control integration.</summary>
    public ObservableCollection<string> MidiPorts { get; } = new();

    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;
    [ObservableProperty] private string _status = "Audio not started";
    [ObservableProperty] private bool _toneEnabled;
    [ObservableProperty] private double _frequencyLog2 = Math.Log2(220.0);
    [ObservableProperty] private double _gainDb = -18.0;

    public double FrequencyHz => Math.Pow(2.0, FrequencyLog2);

    // --- MIDI / Launchkey ---
    private PortWatcher? _portWatcher;
    private ControllerInput? _controller;
    private int _heldNote = -1;

    [ObservableProperty] private string _launchkeyStatus = "Launchkey: searching…";
    [ObservableProperty] private string _lastMidiEvent = "";

    public MainViewModel()
    {
        StartMidi();
        _callback.Engine = _engine;
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
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (!_opening && value != null) OpenStream();
    }

    partial void OnToneEnabledChanged(bool value) => _engine.Enabled = value;

    partial void OnFrequencyLog2Changed(double value)
    {
        _engine.Frequency = (float)Math.Pow(2.0, value);
        OnPropertyChanged(nameof(FrequencyHz));
    }

    partial void OnGainDbChanged(double value) => _engine.GainDb = (float)value;

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

            // Fresh engine at the actual sample rate, carrying over the current settings.
            var engine = new TestToneEngine(rate)
            {
                Frequency = (float)FrequencyHz,
                GainDb = (float)GainDb,
                Enabled = ToneEnabled,
            };
            _engine = engine;
            _callback.Engine = engine;

            _audio.Start();
            Status = $"{_audio.SampleRate} Hz, {_audio.FramesPerBuffer} frames, reported output latency {_audio.OutputLatencyMs:0.0} ms";
        }
        catch (Exception ex)
        {
            Status = "Audio error: " + ex.Message;
        }
    }

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
            _controller = new ControllerInput(ports);
            // MIDI events arrive on a driver thread; marshal to the UI thread (M0 only — the real
            // engine will get notes through a lock-free queue, see PLAN.md section 3).
            _controller.NoteOn += n => Dispatcher.UIThread.Post(() => OnNoteOn(n));
            _controller.NoteOff += n => Dispatcher.UIThread.Post(() => OnNoteOff(n));
            _controller.ControlChange += c => Dispatcher.UIThread.Post(() =>
                LastMidiEvent = $"CC {c.Controller} = {c.Value}  ({c.Source}, ch {c.Channel + 1})");
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
        if (_heldNote >= 0) { _heldNote = -1; ToneEnabled = false; }
    }

    // M0 demo: the Launchkey keys play the test tone (monophonic, last-note priority).
    private void OnNoteOn(ControllerNote n)
    {
        LastMidiEvent = $"Note on {n.Note} vel {n.Velocity}  ({n.Source}, ch {n.Channel + 1})";
        if (n.Source != ControllerSource.Keys) return;
        _heldNote = n.Note;
        FrequencyLog2 = Math.Log2(440.0) + (n.Note - 69) / 12.0;
        ToneEnabled = true;
    }

    private void OnNoteOff(ControllerNote n)
    {
        LastMidiEvent = $"Note off {n.Note}  ({n.Source}, ch {n.Channel + 1})";
        if (n.Source == ControllerSource.Keys && n.Note == _heldNote)
        {
            _heldNote = -1;
            ToneEnabled = false;
        }
    }

    public void Dispose()
    {
        _portWatcher?.Dispose();
        _controller?.Dispose();
        _audio.Dispose();
    }

    /// <summary>Adapts the engine to the audio callback; the engine reference is swapped on reopen.</summary>
    private sealed class EngineCallback : IAudioCallback
    {
        public TestToneEngine? Engine;

        public void Render(Span<float> interleavedStereo, int frames)
        {
            var e = Volatile.Read(ref Engine);
            if (e == null) interleavedStereo.Clear();
            else e.Render(interleavedStereo, frames);
        }
    }
}
