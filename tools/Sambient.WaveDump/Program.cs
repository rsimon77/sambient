using Sambient.Dsp.Wavetables;
using Sambient.Engine;

// Sambient.WaveDump: renders a slow HARMONIC sweep 0 -> 127 of a WAVE to a mono 48 kHz WAV, or lists the implemented WAVEs.
const int SampleRate = 48000;

var opts = new Dictionary<string, string>();
bool list = false;
string? demoPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list") list = true;
    else if (args[i] == "--engine-demo" && i + 1 < args.Length) demoPath = args[++i];
    else if (args[i].StartsWith("--") && i + 1 < args.Length) opts[args[i][2..]] = args[++i];
    else { Usage(); return 1; }
}

if (demoPath != null) return RunEngineDemo(demoPath);

if (list)
{
    Console.WriteLine("idx  name   build ms   memory MB  description");
    foreach (var r in WaveRecipes.Implemented)
    {
        var t = WavetableBuilder.Build(r);
        Console.WriteLine($"{r.Index,3}  {r.ShortName,-5} {t.BuildTime.TotalMilliseconds,9:F1} {t.MemoryBytes / 1048576.0,11:F2}  {r.Description}");
    }
    return 0;
}

if (!opts.TryGetValue("wave", out var ws) || !int.TryParse(ws, out int wave) || !opts.TryGetValue("out", out var path)) { Usage(); return 1; }
int note = opts.TryGetValue("note", out var ns) ? int.Parse(ns) : 48;
double seconds = opts.TryGetValue("seconds", out var ss) ? double.Parse(ss, System.Globalization.CultureInfo.InvariantCulture) : 8.0;

if (!WaveRecipes.TryGet(wave, out var recipe)) { Console.Error.WriteLine($"WAVE {wave} is not implemented."); return 2; }
var table = WavetableBuilder.Build(recipe);
double hz = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
int total = (int)(seconds * SampleRate);
var buf = new float[total];
var pos = new float[total];
for (int i = 0; i < total; i++) pos[i] = (float)(127.0 * i / Math.Max(1, total - 1));
var osc = new WavetableOscillator();
osc.SetWave(table);
osc.SetFrequency(hz, SampleRate);
osc.Process(buf, pos);
for (int i = 0; i < total; i++) buf[i] *= 0.8f;
WriteWav16(path, buf, SampleRate);
Console.WriteLine($"{recipe.ShortName}: {seconds:F1}s, MIDI {note} ({hz:F2} Hz), built in {table.BuildTime.TotalMilliseconds:F0} ms, {table.MemoryBytes / 1048576.0:F2} MB -> {path}");
return 0;

static void Usage() => Console.Error.WriteLine("Usage: WaveDump --list | --wave <n> --out <file.wav> [--note 48] [--seconds 8] | --engine-demo <out.wav>");

// Offline AudioEngine render (no audio device): chords on several WAVEs, HARMONIC and CUTOFF sweeps, sustain pedal, mod wheel.
static int RunEngineDemo(string path)
{
    const int fs = 48000, block = 256;
    const double seconds = 20.0;
    var engine = new AudioEngine(fs);
    Task.WaitAll(new[] { 1, 2, 3, 8, 11, 25 }.Select(engine.Bank.EnsureBuiltAsync).ToArray());

    var events = new List<(double t, Action a)>();
    void At(double t, Action a) => events.Add((t, a));
    void P(ParamId id, float v) => engine.SetParam(0, id, v);
    void On(double t, params int[] notes) => At(t, () => { foreach (var n in notes) engine.NoteOn(0, n, 90); });
    void Off(double t, params int[] notes) => At(t, () => { foreach (var n in notes) engine.NoteOff(0, n); });

    // 0-6 s: SIN.S pad, C minor, slow attack
    At(0, () => { P(ParamId.Wave, 3); P(ParamId.Harmonic, 40); P(ParamId.Attack, 85); P(ParamId.Release, 90); P(ParamId.Cutoff, 80); });
    On(0.0, 48, 55, 58, 63);
    Off(5.0, 48, 55, 58, 63);
    // 4-10 s: SAW.S, F major, HARMONIC sweep 0 -> 127 (script below)
    At(4.0, () => { P(ParamId.Wave, 1); P(ParamId.Harmonic, 0); P(ParamId.Cutoff, 70); });
    On(4.0, 41, 53, 60, 65);
    Off(9.0, 41, 53, 60, 65);
    // 8-13 s: EV.OD, Bb, cutoff sweep with resonance
    At(8.0, () => { P(ParamId.Wave, 8); P(ParamId.Harmonic, 20); P(ParamId.Resonance, 70); });
    On(8.0, 46, 53, 58, 62);
    // sustain pedal: pedal down at 10.5, notes released at 11, pedal up at 14
    At(10.5, () => engine.Sustain(true));
    Off(11.0, 46, 53, 58, 62);
    At(14.0, () => engine.Sustain(false));
    // 13-18 s: SIN.F and FM.PF with mod wheel
    At(13.0, () => { P(ParamId.Wave, 11); P(ParamId.Harmonic, 30); P(ParamId.Resonance, 30); P(ParamId.Cutoff, 100); });
    On(13.0, 36, 48, 55, 60);
    At(15.0, () => { P(ParamId.Wave, 25); engine.ModWheel(0.6f); });
    On(15.0, 67);
    At(17.0, () => engine.ModWheel(0f));
    Off(17.5, 36, 48, 55, 60, 67);
    events.Sort((a, b) => a.t.CompareTo(b.t));

    int total = (int)(seconds * fs);
    var all = new float[total * 2];
    var buf = new float[block * 2];
    int next = 0, maxVoices = 0;
    float peak = 0f;
    for (int pos = 0; pos < total; pos += block)
    {
        double t = (double)pos / fs;
        while (next < events.Count && events[next].t <= t) events[next++].a();
        // continuous sweeps
        if (t >= 4 && t < 10) engine.SetParam(0, ParamId.Harmonic, (float)(127.0 * (t - 4) / 6.0));
        if (t >= 8 && t < 13) engine.SetParam(0, ParamId.Cutoff, (float)(30 + 97 * (t - 8) / 5.0));
        int n = Math.Min(block, total - pos);
        engine.Render(buf, n);
        maxVoices = Math.Max(maxVoices, engine.ActiveVoices);
        for (int i = 0; i < n * 2; i++) { all[pos * 2 + i] = buf[i]; peak = Math.Max(peak, Math.Abs(buf[i])); }
    }
    WriteWav16Stereo(path, all, fs);
    Console.WriteLine($"Engine demo: {seconds:F0} s, peak {peak:F3} ({20 * Math.Log10(Math.Max(peak, 1e-9)):F1} dBFS), max voices {maxVoices}, dropped events {engine.DroppedEvents} -> {path}");
    return 0;
}

static void WriteWav16Stereo(string path, float[] interleaved, int sampleRate)
{
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var w = new BinaryWriter(fs);
    int dataBytes = interleaved.Length * 2;
    w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
    w.Write(sampleRate); w.Write(sampleRate * 4); w.Write((short)4); w.Write((short)16);
    w.Write("data"u8); w.Write(dataBytes);
    foreach (var s in interleaved)
        w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f));
}

static void WriteWav16(string path, float[] samples, int sampleRate)
{
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var w = new BinaryWriter(fs);
    int dataBytes = samples.Length * 2;
    w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
    w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8); w.Write(dataBytes);
    foreach (var s in samples)
        w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f));
}
