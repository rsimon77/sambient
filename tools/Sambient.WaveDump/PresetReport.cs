using System.Globalization;
using Sambient.Engine;
using Sambient.Seq;

/// <summary>
/// <c>--preset-report [--bank N] [--seconds S] [--out-dir DIR]</c>: renders every factory palette (all 64 of banks 1..4, or one bank)
/// offline through a fresh engine + sequencer and prints a loudness table. The first <see cref="SkipSeconds"/> of every render are
/// left out of the measurement (pre-roll, attacks). Used to keep the factory palettes at a consistent loudness.
/// </summary>
internal static class PresetReport
{
    private const int Fs = 48000, Block = 256;
    private const double SkipSeconds = 4.0, WindowSeconds = 3.0;

    public readonly record struct Result(double RmsDb, double LoudnessDb, double PeakDb, double LoudestWindowDb, int MaxVoices);

    public static int Run(Dictionary<string, string> opts)
    {
        var inv = CultureInfo.InvariantCulture;
        int bankFilter = opts.TryGetValue("bank", out var bs) ? int.Parse(bs, inv) - 1 : -1;
        double seconds = opts.TryGetValue("seconds", out var ss) ? double.Parse(ss, inv) : 30.0;
        opts.TryGetValue("out-dir", out var outDir);
        if (outDir != null) Directory.CreateDirectory(outDir);

        var bank = new PatternBank();
        Console.WriteLine("slot      name      RMS dB   LK dB   peak dB  loud3s dB  3s-RMS  voices");
        double sum = 0; int count = 0;
        for (int b = 0; b < PatternBank.PresetBanks; b++)
        {
            if (bankFilter >= 0 && b != bankFilter) continue;
            for (int s = 0; s < PatternBank.SlotsPerBank; s++)
            {
                var p = bank.Load(b, s);
                string? wav = outDir == null ? null : Path.Combine(outDir, $"{PatternBank.SlotId(b, s)}-{p.Name.TrimEnd()}.wav");
                var r = Measure(bank, b, s, seconds, SkipSeconds, wav);
                Console.WriteLine($"{PatternBank.SlotId(b, s),-9} {p.Name.TrimEnd(),-8} {r.RmsDb,8:F1} {r.LoudnessDb,7:F1} {r.PeakDb,9:F1} {r.LoudestWindowDb,10:F1} {r.LoudestWindowDb - r.RmsDb,7:F1} {r.MaxVoices,7}");
                sum += r.RmsDb; count++;
            }
        }
        if (count > 0) Console.WriteLine($"mean RMS {sum / count:F1} dBFS over {count} palettes ({seconds:F0} s each, first {SkipSeconds:F0} s skipped)");
        return 0;
    }

    /// <summary>Renders one palette with a fresh engine + sequencer and measures everything after <paramref name="skip"/> seconds.</summary>
    public static Result Measure(PatternBank bank, int b, int s, double seconds, double skip, string? wavPath)
    {
        var engine = new AudioEngine(Fs);
        var seq = new Sequencer(engine, bank);
        var p = bank.Load(b, s);
        var waves = new HashSet<int>(); var noise = new HashSet<int>();
        for (int l = 0; l < 4; l++)
        {
            (l == 3 ? noise : waves).Add((int)p.GetLayer(l, ParamId.Wave));
            foreach (var st in p.Tracks[l].Steps) if (st.Locks.TryGetValue(ParamId.Wave, out var w)) (l == 3 ? noise : waves).Add((int)w);
        }
        Task.WaitAll(waves.Select(engine.Bank.EnsureBuiltAsync).Concat(noise.Select(engine.Noise.EnsureBuiltAsync)).ToArray());

        seq.SelectPalette(b, s);
        var buf = new float[Block * 2];
        engine.Render(buf, Block);
        seq.Poll();
        seq.Play();

        int total = (int)(seconds * Fs), skipN = (int)(skip * Fs);
        var all = new float[total * 2];
        int maxVoices = 0;
        for (int pos = 0; pos < total; pos += Block)
        {
            int n = Math.Min(Block, total - pos);
            engine.Render(buf, n);
            seq.Poll();
            if (pos >= skipN) maxVoices = Math.Max(maxVoices, engine.ActiveVoices);
            Array.Copy(buf, 0, all, pos * 2, n * 2);
        }
        if (wavPath != null) WriteWav(wavPath, all);

        // measurement after the skipped pre-roll
        int m = total - skipN;
        double sumSq = 0; float peak = 0;
        var perFrame = new double[m];                 // L^2 + R^2 per frame
        for (int i = 0; i < m; i++)
        {
            float l = all[(skipN + i) * 2], r = all[(skipN + i) * 2 + 1];
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            perFrame[i] = (double)l * l + (double)r * r;
            sumSq += perFrame[i];
        }
        double rms = Math.Sqrt(sumSq / (2.0 * m));
        // loudest 3 s window (hop 0.5 s) of the plain RMS
        int win = (int)(WindowSeconds * Fs), hop = Fs / 2;
        double loudest = rms;
        if (m >= win)
        {
            loudest = 0;
            double acc = 0; for (int i = 0; i < win; i++) acc += perFrame[i];
            for (int st = 0; ; st += hop)
            {
                if (st > 0) { acc = 0; for (int i = st; i < st + win; i++) acc += perFrame[i]; }
                loudest = Math.Max(loudest, Math.Sqrt(acc / (2.0 * win)));
                if (st + hop + win > m) break;
            }
        }
        return new Result(Db(rms), KWeightedDb(all, skipN, m), Db(peak), Db(loudest), maxVoices);
    }

    private static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-9));

    /// <summary>ITU-R BS.1770 K-weighting (48 kHz coefficients) integrated loudness without gating, in LKFS-like dB.</summary>
    private static double KWeightedDb(float[] interleaved, int startFrame, int frames)
    {
        double total = 0;
        for (int ch = 0; ch < 2; ch++)
        {
            double s1x1 = 0, s1x2 = 0, s1y1 = 0, s1y2 = 0, s2x1 = 0, s2x2 = 0, s2y1 = 0, s2y2 = 0, ss = 0;
            for (int i = 0; i < frames; i++)
            {
                double x = interleaved[(startFrame + i) * 2 + ch];
                double y = 1.53512485958697 * x - 2.69169618940638 * s1x1 + 1.19839281085285 * s1x2 + 1.69065929318241 * s1y1 - 0.73248077421585 * s1y2;
                s1x2 = s1x1; s1x1 = x; s1y2 = s1y1; s1y1 = y;
                double z = y - 2 * s2x1 + s2x2 + 1.99004745483398 * s2y1 - 0.99007225036621 * s2y2;
                s2x2 = s2x1; s2x1 = y; s2y2 = s2y1; s2y1 = z;
                ss += z * z;
            }
            total += ss / frames;
        }
        return -0.691 + 10 * Math.Log10(Math.Max(total, 1e-18));
    }

    private static void WriteWav(string path, float[] interleaved)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataBytes = interleaved.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(Fs); w.Write(Fs * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var x in interleaved) w.Write((short)Math.Round(Math.Clamp(x, -1f, 1f) * 32767f));
    }
}
