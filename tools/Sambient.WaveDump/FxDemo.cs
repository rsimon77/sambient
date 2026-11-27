using Sambient.Dsp.Effects;

/// <summary>Offline render of every reverb type and master FX on plucked chords and noise bursts (no audio device).</summary>
internal static class FxDemo
{
    private const int Fs = 48000, Block = 256;

    public static int Run(string path)
    {
        var reverb = new ShimmerReverb(Fs);
        var master = new MasterFx(Fs);
        var rng = new Random(42);

        var segments = new List<(string Name, double Len, Action Setup, bool UseReverb, bool Shimmer)>();
        void Rev(ReverbType t, bool shimmer, float mix = 100f) =>
            segments.Add(($"reverb {t}{(shimmer ? " + shimmer send" : "")}, mix {mix:F0}", 2.0,
                () => { master.SetType(MasterFxType.Off); reverb.SetType(t); reverb.SetMix(mix); }, true, shimmer));
        Rev(ReverbType.SmallL, false); Rev(ReverbType.SmallM, false); Rev(ReverbType.SmallH, false);
        Rev(ReverbType.LargeL, false); Rev(ReverbType.LargeM, true); Rev(ReverbType.LargeH, true);
        Rev(ReverbType.InfinityL, false, 80f); Rev(ReverbType.InfinityM, true, 80f); Rev(ReverbType.InfinityH, true, 80f);

        void Fx(MasterFxType t, float speed, float amount) =>
            segments.Add(($"master fx {t}, speed {speed:F0}, amount {amount:F0}", 2.2,
                () => { reverb.SetType(ReverbType.Off); master.SetType(t); master.SetSpeed(speed); master.SetAmount(amount); }, false, false));
        Fx(MasterFxType.TapeDelay, 64f, 70f);
        Fx(MasterFxType.ReverseDelay, 40f, 80f);
        Fx(MasterFxType.Overdrive, 90f, 100f);
        Fx(MasterFxType.Crush, 80f, 127f);
        Fx(MasterFxType.TiltEq, 40f, 10f);
        Fx(MasterFxType.Chorus, 45f, 100f);

        var chords = new[] { new[] { 48, 55, 63, 70 }, new[] { 53, 60, 65, 69 }, new[] { 46, 58, 62, 69 } };
        var outL = new List<float>(); var outR = new List<float>();
        double t0 = 0;
        int chordIdx = 0;
        foreach (var seg in segments)
        {
            Console.WriteLine($"{t0,5:F1} s  {seg.Name}");
            seg.Setup();
            int n = (int)(seg.Len * Fs);
            var dl = new float[n]; var dr = new float[n];
            // two chord plucks and one noise burst per segment
            AddChord(dl, dr, 0.05, chords[chordIdx++ % chords.Length], rng);
            if (!seg.UseReverb) AddChord(dl, dr, 1.1, chords[chordIdx++ % chords.Length], rng);
            AddNoiseBurst(dl, dr, seg.UseReverb ? 1.3 : 1.7, rng);

            var wl = new float[Block]; var wr = new float[Block]; var zero = new float[Block];
            for (int o = 0; o < n; o += Block)
            {
                int m = Math.Min(Block, n - o);
                var l = dl.AsSpan(o, m); var r = dr.AsSpan(o, m);
                if (seg.UseReverb)
                {
                    var dry = new float[m]; var dryR = new float[m];
                    l.CopyTo(dry); r.CopyTo(dryR);
                    reverb.Process(dry, dryR, seg.Shimmer ? dry : zero.AsSpan(0, m), seg.Shimmer ? dryR : zero.AsSpan(0, m), wl.AsSpan(0, m), wr.AsSpan(0, m));
                    for (int i = 0; i < m; i++) { l[i] = 0.55f * l[i] + wl[i]; r[i] = 0.55f * r[i] + wr[i]; }
                }
                else master.Process(l, r);
            }
            outL.AddRange(dl); outR.AddRange(dr);
            t0 += seg.Len;
        }

        // normalise to -1 dBFS peak
        float peak = 1e-9f;
        for (int i = 0; i < outL.Count; i++) peak = Math.Max(peak, Math.Max(Math.Abs(outL[i]), Math.Abs(outR[i])));
        float g = 0.89f / peak;
        var inter = new float[outL.Count * 2];
        for (int i = 0; i < outL.Count; i++) { inter[2 * i] = outL[i] * g; inter[2 * i + 1] = outR[i] * g; }
        WriteWav(path, inter);
        Console.WriteLine($"FX demo: {t0:F1} s, peak {20 * Math.Log10(peak):F1} dBFS before normalisation -> {path}");
        return 0;
    }

    private static void AddChord(float[] l, float[] r, double start, int[] notes, Random rng)
    {
        int s0 = (int)(start * Fs);
        int k = 0;
        foreach (int note in notes)
        {
            double hz = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            double pan = (k++ / (double)(notes.Length - 1)) * 2 - 1; // spread the voices
            double gl = Math.Cos((pan * 0.5 + 0.5) * Math.PI / 2), gr = Math.Sin((pan * 0.5 + 0.5) * Math.PI / 2);
            int len = (int)(1.6 * Fs);
            for (int i = 0; i < len && s0 + i < l.Length; i++)
            {
                double t = (double)i / Fs;
                double env = Math.Min(1.0, t / 0.01) * Math.Exp(-t * 2.6);
                double v = Math.Sin(2 * Math.PI * hz * t) + 0.3 * Math.Sin(2 * Math.PI * 2 * hz * t) + 0.1 * Math.Sin(2 * Math.PI * 3 * hz * t);
                float x = (float)(0.16 * env * v);
                l[s0 + i] += x * (float)gl * 1.4f; r[s0 + i] += x * (float)gr * 1.4f;
            }
        }
    }

    private static void AddNoiseBurst(float[] l, float[] r, double start, Random rng)
    {
        int s0 = (int)(start * Fs), len = (int)(0.09 * Fs);
        float lp = 0;
        for (int i = 0; i < len && s0 + i < l.Length; i++)
        {
            float env = (float)Math.Exp(-i / (0.02 * Fs)) * Math.Min(1f, i / 48f);
            float n = (float)(rng.NextDouble() * 2 - 1);
            lp += 0.35f * (n - lp);
            l[s0 + i] += 0.45f * env * lp; r[s0 + i] += 0.45f * env * lp;
        }
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
        foreach (var s in interleaved) w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f));
    }
}
