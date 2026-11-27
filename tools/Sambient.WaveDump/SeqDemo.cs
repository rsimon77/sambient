using Sambient.Engine;
using Sambient.Seq;

/// <summary>Offline render of the step sequencer playing factory palettes (no audio device): POLYMETR, a queued switch to CRYSTAL, a queued switch to GLASSARP.</summary>
internal static class SeqDemo
{
    private const int Fs = 48000, Block = 256;

    public static int Run(string path)
    {
        const double seconds = 45.0;
        var engine = new AudioEngine(Fs);
        var bank = new PatternBank(Path.Combine(Path.GetTempPath(), "sambient-seqdemo-unused"));   // factory presets only, nothing is read or written
        var seq = new Sequencer(engine, bank);
        var log = new List<string>();
        seq.Tap = e =>
        {
            if (e.Kind is SeqTapKind.PaletteSwitch or SeqTapKind.CycleBoundary)
                lock (log) log.Add($"  t={e.Sample / (double)Fs,6:F2}s  {e.Kind}{(e.Kind == SeqTapKind.PaletteSwitch ? $" slot {e.Note}" : $" #{e.Note}")}");
        };

        // palettes: bank 1 slot 12 POLYMETR, slot 9 CRYSTAL, slot 3 GLASSARP (0-based slot indices 11, 8, 2)
        (int Bank, int Slot)[] order = [(0, 11), (0, 8), (0, 2)];
        var waves = new HashSet<int>(); var noise = new HashSet<int>();
        foreach (var (b, s) in order)
        {
            var p = bank.Load(b, s);
            for (int l = 0; l < 4; l++)
            {
                (l == 3 ? noise : waves).Add((int)p.GetLayer(l, ParamId.Wave));
                foreach (var st in p.Tracks[l].Steps) if (st.Locks.TryGetValue(ParamId.Wave, out var w)) (l == 3 ? noise : waves).Add((int)w);
            }
            Console.WriteLine($"palette {PatternBank.SlotId(b, s)} {p.Name.TrimEnd()}");
        }
        Task.WaitAll(waves.Select(engine.Bank.EnsureBuiltAsync).Concat(noise.Select(engine.Noise.EnsureBuiltAsync)).ToArray());

        seq.SelectPalette(order[0].Bank, order[0].Slot);
        engine.Render(new float[Block * 2], Block);
        seq.Poll();
        seq.Play();

        int total = (int)(seconds * Fs);
        var all = new float[total * 2];
        var buf = new float[Block * 2];
        int maxVoices = 0; float peak = 0; double sumSq = 0;
        bool queued1 = false, queued2 = false;
        for (int pos = 0; pos < total; pos += Block)
        {
            double t = (double)pos / Fs;
            if (!queued1 && t >= 3.0) { seq.SelectPalette(order[1].Bank, order[1].Slot); queued1 = true; }       // switches when POLYMETR's cycle ends
            if (!queued2 && t >= 20.0) { seq.SelectPalette(order[2].Bank, order[2].Slot); queued2 = true; }      // switches when CRYSTAL's cycle ends
            int n = Math.Min(Block, total - pos);
            engine.Render(buf, n);
            seq.Poll();
            maxVoices = Math.Max(maxVoices, engine.ActiveVoices);
            for (int i = 0; i < n * 2; i++) { all[pos * 2 + i] = buf[i]; peak = Math.Max(peak, Math.Abs(buf[i])); sumSq += (double)buf[i] * buf[i]; }
        }
        WriteWav16Stereo(path, all, Fs);
        double rms = Math.Sqrt(sumSq / all.Length);
        foreach (var l in log) Console.WriteLine(l);
        Console.WriteLine($"Seq demo: {seconds:F0} s, peak {peak:F3} ({20 * Math.Log10(Math.Max(peak, 1e-9)):F1} dBFS), RMS {20 * Math.Log10(Math.Max(rms, 1e-9)):F1} dBFS, "
                          + $"max voices {maxVoices}, final palette {seq.CurrentName.TrimEnd()} -> {path}");
        return 0;
    }

    private static void WriteWav16Stereo(string path, float[] interleaved, int sampleRate)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataBytes = interleaved.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(sampleRate); w.Write(sampleRate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in interleaved) w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f));
    }
}
