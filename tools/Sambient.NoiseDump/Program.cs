using System.Diagnostics;
using Sambient.Dsp.Noise;

// Sambient.NoiseDump: --list (name, build ms, RMS, peak) and --out <dir> (16-bit stereo 32 kHz WAVs).
bool list = args.Contains("--list");
string? outDir = null;
for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--out") outDir = args[i + 1];
if (!list && outDir == null) list = true;

if (outDir != null) Directory.CreateDirectory(outDir);
Console.WriteLine($"{"slot",-5}{"name",-6}{"ms",8}{"rms dB",9}{"peak dB",9}{"seam step",11}");
for (int slot = 1; slot <= NoiseBank.SlotCount; slot++)
{
    var sw = Stopwatch.StartNew();
    var s = NoiseTextures.Generate(slot);
    sw.Stop();
    double e = 0, pk = 0;
    for (int i = 0; i < s.Length; i++)
    {
        e += (double)s.Left[i] * s.Left[i] + (double)s.Right[i] * s.Right[i];
        pk = Math.Max(pk, Math.Max(Math.Abs(s.Left[i]), Math.Abs(s.Right[i])));
    }
    double rms = Math.Sqrt(e / (2.0 * s.Length));
    double seam = Math.Abs(s.Left[^1] - s.Left[0]);
    Console.WriteLine($"{slot,-5}{s.Name,-6}{sw.ElapsedMilliseconds,8}{20 * Math.Log10(rms),9:F1}{20 * Math.Log10(pk),9:F1}{seam,11:F5}");
    if (outDir != null)
    {
        WriteWav(Path.Combine(outDir, $"{slot:D2}_{s.Name}.wav"), s, 1);
        WriteWav(Path.Combine(outDir, $"{slot:D2}_{s.Name}_x3.wav"), s, 3);
    }
}
if (outDir != null) Console.WriteLine($"WAVs written to {outDir}");

static void WriteWav(string path, NoiseSample s, int repeats)
{
    int frames = s.Length * repeats;
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    int dataBytes = frames * 4;
    w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
    w.Write(s.SampleRate); w.Write(s.SampleRate * 4); w.Write((short)4); w.Write((short)16);
    w.Write("data"u8); w.Write(dataBytes);
    for (int rep = 0; rep < repeats; rep++)
        for (int i = 0; i < s.Length; i++)
        {
            w.Write((short)Math.Clamp((int)Math.Round(s.Left[i] * 32767.0), -32768, 32767));
            w.Write((short)Math.Clamp((int)Math.Round(s.Right[i] * 32767.0), -32768, 32767));
        }
}
