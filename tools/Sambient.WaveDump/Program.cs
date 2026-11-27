using Sambient.Dsp.Effects;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;

// Sambient.WaveDump: renders a slow HARMONIC sweep 0 -> 127 of a WAVE to a mono 48 kHz WAV, or lists the implemented WAVEs.
const int SampleRate = 48000;

var opts = new Dictionary<string, string>();
bool list = false, presetReport = false;
string? demoPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list") list = true;
    else if (args[i] == "--preset-report") presetReport = true;
    else if (args[i] == "--engine-demo" && i + 1 < args.Length) demoPath = args[++i];
    else if (args[i].StartsWith("--") && i + 1 < args.Length) opts[args[i][2..]] = args[++i];
    else { Usage(); return 1; }
}

if (presetReport) return PresetReport.Run(opts);
if (demoPath != null) return RunEngineDemo(demoPath);
if (opts.TryGetValue("fx-demo", out var fxDemoPath)) return FxDemo.Run(fxDemoPath);
if (opts.TryGetValue("seq-demo", out var seqDemoPath)) return SeqDemo.Run(seqDemoPath);

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

// Offline AudioEngine render (no audio device): all four layers (drone, pad, bell, noise), reverb with shimmer sends and a
// master FX, driven by a small script. ~30 s. The reverb / FX are silent / passthrough while their DSP is still a stub.
static int RunEngineDemo(string path)
{
    const int fs = 48000, block = 256;
    const double seconds = 30.0;
    var parameters = new ParameterStore();
    DefaultPatch.Apply(parameters);
    var engine = new AudioEngine(fs, parameters);
    Task.WaitAll(new[] { 3, 8, 25 }.Select(engine.Bank.EnsureBuiltAsync).Append(engine.Noise.EnsureBuiltAsync(1)).ToArray());

    var events = new List<(double t, Action a)>();
    void At(double t, Action a) => events.Add((t, a));
    void On(double t, int layer, int vel, params int[] notes) => At(t, () => { foreach (var n in notes) engine.NoteOn(layer, n, vel); });
    void Off(double t, int layer, params int[] notes) => At(t, () => { foreach (var n in notes) engine.NoteOff(layer, n); });

    // L4 NOISE: RAIN loop (or white noise only while the sample bank is a stub) held for the whole piece
    On(0.0, 3, 80, 60);
    Off(27.0, 3, 60);
    // L1 DRONE: SIN.S, C2 + G2
    On(0.5, 0, 90, 36, 43);
    Off(17.0, 0, 36, 43);
    On(17.5, 0, 90, 32, 39);                  // Ab1 + Eb2
    Off(27.0, 0, 32, 39);
    // L2 PAD: EV.OD, Cm -> Ab, shimmer builds up
    On(3.0, 1, 85, 60, 63, 67);
    Off(15.0, 1, 60, 63, 67);
    On(15.5, 1, 85, 56, 60, 63);
    Off(26.0, 1, 56, 60, 63);
    // L3 BELL: FM.PF, sparse notes
    double[] bellTimes = { 6, 8.5, 11, 12.5, 16, 18.5, 20, 22.5, 24 };
    int[] bell = { 84, 79, 75, 82, 80, 75, 84, 87, 79 };
    for (int i = 0; i < bellTimes.Length; i++)
    {
        On(bellTimes[i], 2, 70 + (i % 3) * 12, bell[i]);
        Off(bellTimes[i] + 0.6, 2, bell[i]);
    }
    // global: master FX and reverb changes
    At(12.0, () => engine.SetGlobal(GlobalParamId.FxAmount, 60));
    At(20.0, () => { engine.SetGlobal(GlobalParamId.ReverbType, (float)ReverbType.InfinityM); engine.SetGlobal(GlobalParamId.ReverbMix, 90); });
    At(22.0, () => { engine.SetGlobal(GlobalParamId.FxType, (float)MasterFxType.Chorus); engine.SetGlobal(GlobalParamId.FxSpeed, 40); engine.SetGlobal(GlobalParamId.FxAmount, 50); });
    At(26.0, () => engine.SetGlobal(GlobalParamId.FxType, (float)MasterFxType.TapeDelay));
    events.Sort((a, b) => a.t.CompareTo(b.t));

    int total = (int)(seconds * fs);
    var all = new float[total * 2];
    var buf = new float[block * 2];
    int next = 0, maxVoices = 0;
    float peak = 0f;
    double sumSq = 0;
    for (int pos = 0; pos < total; pos += block)
    {
        double t = (double)pos / fs;
        while (next < events.Count && events[next].t <= t) events[next++].a();
        // continuous sweeps: pad shimmer send 35 -> 100 (10-20 s), drone cutoff opening 70 -> 100 (0-15 s)
        if (t >= 10 && t < 20) engine.SetParam(1, ParamId.ShimmerSend, (float)(35 + 65 * (t - 10) / 10.0));
        if (t < 15) engine.SetParam(0, ParamId.Cutoff, (float)(70 + 30 * t / 15.0));
        int n = Math.Min(block, total - pos);
        engine.Render(buf, n);
        maxVoices = Math.Max(maxVoices, engine.ActiveVoices);
        for (int i = 0; i < n * 2; i++) { all[pos * 2 + i] = buf[i]; peak = Math.Max(peak, Math.Abs(buf[i])); sumSq += (double)buf[i] * buf[i]; }
    }
    WriteWav16Stereo(path, all, fs);
    double rms = Math.Sqrt(sumSq / all.Length);
    Console.WriteLine($"Engine demo: {seconds:F0} s, 4 layers, peak {peak:F3} ({20 * Math.Log10(Math.Max(peak, 1e-9)):F1} dBFS), "
                      + $"RMS {20 * Math.Log10(Math.Max(rms, 1e-9)):F1} dBFS, max voices {maxVoices}, dropped events {engine.DroppedEvents} -> {path}");
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
