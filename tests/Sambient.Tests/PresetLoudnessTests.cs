using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.Tests;

/// <summary>Factory palettes must have a consistent loudness (see <c>WaveDump --preset-report</c>): about -24 dBFS RMS, peak below the soft clipper.</summary>
public class PresetLoudnessTests
{
    private const int Fs = 48000, Block = 256;
    // Shortened from the report tool's 30 s / 4 s skip to keep the test fast: 12 s per palette, the first 3 s are not measured.
    private const double Seconds = 12.0, SkipSeconds = 3.0;

    private static (double rmsDb, double peakDb) Render(PatternBank bank, int slot)
    {
        var engine = new AudioEngine(Fs);
        var seq = new Sequencer(engine, bank);
        var p = bank.Load(0, slot);
        var waves = new HashSet<int>(); var noise = new HashSet<int>();
        for (int l = 0; l < 4; l++)
        {
            (l == 3 ? noise : waves).Add((int)p.GetLayer(l, ParamId.Wave));
            foreach (var st in p.Tracks[l].Steps) if (st.Locks.TryGetValue(ParamId.Wave, out var w)) (l == 3 ? noise : waves).Add((int)w);
        }
        Task.WaitAll(waves.Select(engine.Bank.EnsureBuiltAsync).Concat(noise.Select(engine.Noise.EnsureBuiltAsync)).ToArray());

        seq.SelectPalette(0, slot);
        var buf = new float[Block * 2];
        engine.Render(buf, Block);
        seq.Poll();
        seq.Play();

        int total = (int)(Seconds * Fs), skip = (int)(SkipSeconds * Fs);
        double sumSq = 0; float peak = 0; long count = 0;
        for (int pos = 0; pos < total; pos += Block)
        {
            int n = Math.Min(Block, total - pos);
            engine.Render(buf, n);
            seq.Poll();
            if (pos < skip) continue;
            for (int i = 0; i < n * 2; i++) { peak = Math.Max(peak, Math.Abs(buf[i])); sumSq += (double)buf[i] * buf[i]; count++; }
        }
        return (20 * Math.Log10(Math.Max(Math.Sqrt(sumSq / count), 1e-9)), 20 * Math.Log10(Math.Max(peak, 1e-9)));
    }

    [Fact]
    public void HandDesignedPalettes_HaveConsistentLoudness()
    {
        var bank = new PatternBank();   // factory presets only
        var failures = new List<string>();
        for (int slot = 0; slot < 12; slot++)
        {
            var (rms, peak) = Render(bank, slot);
            string name = bank.Load(0, slot).Name.TrimEnd();
            if (rms is < -28 or > -21) failures.Add($"{name}: RMS {rms:F1} dBFS outside [-28, -21]");
            if (peak > -2) failures.Add($"{name}: peak {peak:F1} dBFS above -2");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
