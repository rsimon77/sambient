using System.Globalization;
using Sambient.Dsp.Effects;
using Sambient.Dsp.Noise;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.Tools;

/// <summary>One click-audit measurement: the discontinuity of the output around a single parameter change.</summary>
/// <param name="Name">Scenario name.</param>
/// <param name="BlockDb">spikiness of the output (see <see cref="ClickAudit.Spikiness"/>) in the 7 ms around the change vs. the steady state before it, in dB.</param>
/// <param name="SettleDb">the same metric over the 80 ms after the change (also catches the end of a ramp).</param>
/// <param name="RawBlockDb">plain max |second difference| around the change vs. steady state, dB (also grows with legit gain / brightness changes).</param>
/// <param name="RawSettleDb">plain max |second difference| over the 80 ms after the change vs. steady state, dB.</param>
public sealed record ClickResult(string Name, double BlockDb, double SettleDb, double RawBlockDb, double RawSettleDb)
{
    public double WorstDb => Math.Max(BlockDb, SettleDb);
}

/// <summary>One parameter change on top of the standard musical texture.</summary>
public sealed class AuditScenario
{
    public required string Name { get; init; }
    /// <summary>Applied to the parameter store before the engine exists (initial state of the scenario).</summary>
    public Action<ParameterStore>? Setup { get; init; }
    /// <summary>The ONE change, applied between two render blocks (the engine sees it at the start of the next block).</summary>
    public required Action<AudioEngine> Change { get; init; }
    /// <summary>Allowed worst-case ratio in dB (default +6); a documented exception raises it.</summary>
    public double LimitDb { get; init; } = ClickAudit.DefaultLimitDb;
    public string? Note { get; init; }
}

/// <summary>
/// Offline click audit. Renders a sustained texture (PAD1 chord, DRONE1, ATMOS2 bell, noise sample loop; reverb / FX off or the
/// reverb on), changes ONE parameter at a known frame and compares the "spikiness" (|second difference| relative to its local RMS, per channel) in a short
/// window around the change with the same metric over the last 500 ms of steady state before it (plain max |d2| is reported too). Ratio in dB; above
/// <see cref="DefaultLimitDb"/> is a click. Used by the <c>--click-audit</c> option of WaveDump and by the regression tests.
/// </summary>
public static class ClickAudit
{
    public const int Fs = 48000, Block = 256;
    public const double DefaultLimitDb = 6.0;
    public static bool Diag, NoReverbReset;
    /// <summary>Diagnostics: mute every layer but this one (-1 = all play).</summary>
    public static int SoloLayer = -1;
    public static string? LastLocate;
    private const double WarmSeconds = 1.6, AfterSeconds = 0.45;

    private static readonly object Gate = new();
    private static WavetableBank? _bank;
    private static NoiseBank? _noise;

    /// <summary>Shared, fully built wavetable / noise banks (only the waves the scenarios use are built).</summary>
    public static (WavetableBank, NoiseBank) Banks()
    {
        lock (Gate)
        {
            if (_bank == null)
            {
                _bank = new WavetableBank(); _noise = new NoiseBank();
                var t = new List<Task>();
                foreach (int w in new[] { 1, 3, 8, 12, 14, 25 }) t.Add(_bank.EnsureBuiltAsync(w));
                foreach (int s in new[] { 1, 2 }) t.Add(_noise.EnsureBuiltAsync(s));
                // the palette scenarios need every wave / slot the factory presets use
                for (int w = 1; w <= 32; w++) t.Add(_bank.EnsureBuiltAsync(w));
                for (int s = 1; s <= 8; s++) t.Add(_noise.EnsureBuiltAsync(s));
                Task.WaitAll(t.ToArray());
            }
            return (_bank, _noise!);
        }
    }

    // ------------------------------------------------------------------ standard texture

    /// <summary>The standard texture: the default patch (L2 = PAD1 is the layer most scenarios change), FX off.</summary>
    public static void TextureParams(ParameterStore p, bool reverb)
    {
        DefaultPatch.Apply(p);
        p.Set(GlobalParamId.FxType, 0);
        p.Set(GlobalParamId.ReverbType, reverb ? (float)ReverbType.LargeM : 0f);
        for (int l = 0; l < 4; l++) p.Set(l, ParamId.Release, 60);
    }

    private static readonly int[] DroneNotes = { 43, 50 }, PadNotes = { 60, 64, 67, 71 };

    public static void TextureNotes(AudioEngine e)
    {
        foreach (int n in DroneNotes) e.NoteOn(0, n, 100);
        foreach (int n in PadNotes) e.NoteOn(1, n, 100);
        e.NoteOn(2, 72, 100);
        e.NoteOn(3, 60, 100);
    }

    // ------------------------------------------------------------------ measurement

    /// <summary>max |second difference| over [from, to) frames, both channels.</summary>
    public static double MaxD2(ReadOnlySpan<float> stereo, int from, int to)
    {
        int frames = stereo.Length / 2;
        from = Math.Max(from, 1); to = Math.Min(to, frames - 1);
        double m = 0;
        for (int ch = 0; ch < 2; ch++)
            for (int i = from; i < to; i++)
            {
                double d = stereo[2 * (i + 1) + ch] - 2.0 * stereo[2 * i + ch] + stereo[2 * (i - 1) + ch];
                d = Math.Abs(d);
                if (d > m) m = d;
            }
        return m;
    }

    /// <summary>
    /// Largest "spikiness" over [from, to): |d2[i]| divided by the RMS of d2 in the +-64 frames around i (excluding i +-3), both
    /// channels. A single-sample step (click) gives a huge value even when the whole signal got louder or brighter, which a plain
    /// max |d2| cannot tell from a legitimate level / cutoff change.
    /// </summary>
    public static double Spikiness(ReadOnlySpan<float> stereo, int from, int to)
    {
        int frames = stereo.Length / 2;
        from = Math.Max(from, 1); to = Math.Min(to, frames - 1);
        const int R = 64, X = 3;
        double worst = 0;
        int lo = Math.Max(1, from - R - 1), hi = Math.Min(frames - 1, to + R + 1);
        var d = new double[hi - lo];
        var cum = new double[hi - lo + 1];
        for (int ch = 0; ch < 2; ch++)
        {
            for (int i = lo; i < hi; i++)
            {
                double v = stereo[2 * (i + 1) + ch] - 2.0 * stereo[2 * i + ch] + stereo[2 * (i - 1) + ch];
                d[i - lo] = v;
                cum[i - lo + 1] = cum[i - lo] + v * v;
            }
            for (int i = from; i < to; i++)
            {
                int k = i - lo;
                int a = Math.Max(0, k - R), b = Math.Min(d.Length, k + R + 1);
                double sum = cum[b] - cum[a];
                int cnt = b - a;
                int xa = Math.Max(0, k - X), xb = Math.Min(d.Length, k + X + 1);
                sum -= cum[xb] - cum[xa]; cnt -= xb - xa;
                double rms = Math.Sqrt(Math.Max(sum, 0) / Math.Max(cnt, 1));
                double r = Math.Abs(d[k]) / Math.Max(rms, 1e-6);
                if (r > worst) worst = r;
            }
        }
        return worst;
    }

    /// <summary>Diagnostics: frame (relative to <paramref name="changeFrame"/>) and value of the worst spike in the 80 ms after the change.</summary>
    public static string Locate(ReadOnlySpan<float> stereo, int changeFrame)
    {
        int from = changeFrame - 48, to = changeFrame + Fs * 80 / 1000;
        double best = 0; int bi = 0, bc = 0;
        for (int ch = 0; ch < 2; ch++)
            for (int i = Math.Max(from, 2); i < Math.Min(to, stereo.Length / 2 - 2); i++)
            {
                double d = Math.Abs(stereo[2 * (i + 1) + ch] - 2.0 * stereo[2 * i + ch] + stereo[2 * (i - 1) + ch]);
                if (d > best) { best = d; bi = i; bc = ch; }
            }
        if (Environment.GetEnvironmentVariable("CLICKDUMP2") != null)
        {
            for (int i = changeFrame - 64; i < changeFrame + 1100; i += 8) Console.Write($"{(i - changeFrame)}:{stereo[2 * i + bc]:F3} ");
            Console.WriteLine();
        }
        if (Environment.GetEnvironmentVariable("CLICKDUMP") != null)
            for (int i = bi - 12; i <= bi + 12; i++) Console.Write($"{stereo[2 * i + bc]:F4} ");
        return $"worst |d2|={best:F5} at {(bi - changeFrame) * 1000.0 / Fs:F2} ms ch{bc}, x[-1..1]={stereo[2 * (bi - 1) + bc]:F4},{stereo[2 * bi + bc]:F4},{stereo[2 * (bi + 1) + bc]:F4}";
    }

    public static ClickResult Measure(string name, ReadOnlySpan<float> stereo, int changeFrame)
    {
        int pa = changeFrame - Fs / 2, pb = changeFrame - 1;
        int na = changeFrame - 48, nb = changeFrame + 288, sb = changeFrame + Fs * 80 / 1000;
        double pre = Spikiness(stereo, pa, pb);
        double near = Spikiness(stereo, na, nb), settle = Spikiness(stereo, na, sb);
        double rawPre = Math.Max(MaxD2(stereo, pa, pb), 1e-7);
        double rawNear = MaxD2(stereo, na, nb), rawSettle = MaxD2(stereo, na, sb);
        return new ClickResult(name, Db(near / pre), Db(settle / pre), Db(rawNear / rawPre), Db(rawSettle / rawPre));
    }

    private static double Db(double ratio) => 20.0 * Math.Log10(Math.Max(ratio, 1e-9));

    /// <summary>Renders one scenario and measures it.</summary>
    public static ClickResult Run(AuditScenario s, bool reverb)
    {
        var (bank, noise) = Banks();
        var p = new ParameterStore();
        TextureParams(p, reverb);
        s.Setup?.Invoke(p);
        if (SoloLayer >= 0) for (int l = 0; l < 4; l++) p.Set(l, ParamId.Mute, l == SoloLayer ? 0 : 1);
        var e = new AudioEngine(Fs, p, bank, noise);
        TextureNotes(e);
        int warm = (int)(WarmSeconds * Fs), after = (int)(AfterSeconds * Fs);
        var buf = new float[(warm + after) * 2];
        int pos = 0;
        void Render(int frames)
        {
            int end = pos + frames;
            while (pos < end)
            {
                int n = Math.Min(Block, end - pos);
                e.Render(buf.AsSpan(pos * 2, n * 2), n);
                pos += n;
            }
        }
        Render(warm);
        int change = pos;
        s.Change(e);
        Render(after);
        if (Diag) LastLocate = Locate(buf, change);
        return Measure(s.Name, buf, change);
    }

    // ------------------------------------------------------------------ scenarios

    private const int Pad = 1, Noise = 3, Drone = 0;

    private static AuditScenario S(string name, Action<ParameterStore>? setup, Action<AudioEngine> change, double limit = DefaultLimitDb, string? note = null)
        => new() { Name = name, Setup = setup, Change = change, LimitDb = limit, Note = note };

    /// <summary>Parameter scenario on a layer: <paramref name="from"/> in the setup, <paramref name="to"/> as the change.</summary>
    private static AuditScenario P(string name, int layer, ParamId id, float from, float to, double limit = DefaultLimitDb, string? note = null)
        => S(name, p => p.Set(layer, id, from), e => e.SetParam(layer, id, to), limit, note);

    private static AuditScenario G(string name, GlobalParamId id, float from, float to, Action<ParameterStore>? extra = null, double limit = DefaultLimitDb)
        => S(name, p => { p.Set(id, from); extra?.Invoke(p); }, e => e.SetGlobal(id, to), limit);

    public static List<AuditScenario> Scenarios()
    {
        var l = new List<AuditScenario>();

        // ---- WAVE (synth + noise slot)
        foreach (var (a, b) in new[] { (8, 3), (3, 8), (1, 25), (25, 12), (14, 1), (12, 14) })
            l.Add(P($"Wave {a}->{b} (synth)", Pad, ParamId.Wave, a, b));
        l.Add(P("Wave 1->2 (noise slot)", Noise, ParamId.Wave, 1, 2));
        l.Add(P("Wave 2->1 (noise slot)", Noise, ParamId.Wave, 2, 1));

        // ---- STRUCTURE (every ordered pair, on the PAD layer with a chord)
        foreach (Structure a in Enum.GetValues<Structure>())
            foreach (Structure b in Enum.GetValues<Structure>())
                if (a != b)
                    l.Add(S($"Structure {a}->{b}", p => p.Set(Pad, ParamId.Structure, (float)a), e => e.SetParam(Pad, ParamId.Structure, (float)b)));

        // ---- FILTER TYPE (all transitions)
        foreach (FilterType a in Enum.GetValues<FilterType>())
            foreach (FilterType b in Enum.GetValues<FilterType>())
                if (a != b)
                {
                    l.Add(P($"FilterType {a}->{b} (synth)", Pad, ParamId.FilterType, (float)a, (float)b));
                    l.Add(P($"FilterType {a}->{b} (noise)", Noise, ParamId.FilterType, (float)a, (float)b));
                }

        // ---- MOD shape, LFO shape / assign / trig
        foreach (var (a, b) in new[] { (LfoShape.Sine, LfoShape.Square), (LfoShape.Square, LfoShape.Sine), (LfoShape.Triangle, LfoShape.Saw),
                     (LfoShape.Sine, LfoShape.Random), (LfoShape.Pulse10, LfoShape.Step4), (LfoShape.RampUp, LfoShape.LowSine) })
            l.Add(S($"ModShape {a}->{b}", p => { p.Set(Pad, ParamId.ModShape, (float)a); p.Set(Pad, ParamId.ModDepth, 90); p.Set(Pad, ParamId.ModRate, 50); },
                e => e.SetParam(Pad, ParamId.ModShape, (float)b)));
        for (int which = 1; which <= 2; which++)
        {
            int w = which;
            ParamId shape = w == 1 ? ParamId.Lfo1Shape : ParamId.Lfo2Shape, assign = w == 1 ? ParamId.Lfo1Assign : ParamId.Lfo2Assign,
                trig = w == 1 ? ParamId.Lfo1Trig : ParamId.Lfo2Trig, depth = w == 1 ? ParamId.Lfo1Depth : ParamId.Lfo2Depth,
                rate = w == 1 ? ParamId.Lfo1Rate : ParamId.Lfo2Rate;
            void Base(ParameterStore p, LfoAssign a, LfoShape sh = LfoShape.Triangle, int tg = 0)
            {
                p.Set(Pad, assign, (float)a); p.Set(Pad, shape, (float)sh); p.Set(Pad, trig, tg); p.Set(Pad, depth, 90); p.Set(Pad, rate, 70);
                if (w == 2) { p.Set(Pad, ParamId.Lfo1Assign, (float)LfoAssign.Cutoff); p.Set(Pad, ParamId.Lfo1Depth, 20); }
            }
            foreach (var (a, b) in new[] { (LfoShape.Triangle, LfoShape.Square), (LfoShape.Sine, LfoShape.Saw), (LfoShape.Square, LfoShape.Sine),
                         (LfoShape.Random, LfoShape.Pulse25), (LfoShape.Sine, LfoShape.Step3), (LfoShape.RampUp, LfoShape.SmoothRandom) })
                foreach (var tgt in new[] { LfoAssign.Cutoff, LfoAssign.Pan, LfoAssign.Level, LfoAssign.Pitch, LfoAssign.Harmonic })
                    l.Add(S($"Lfo{w}Shape {a}->{b} @{tgt}", p => Base(p, tgt, a), e => e.SetParam(Pad, shape, (float)b)));
            // assign: OFF -> X and X -> OFF for every destination, X -> next destination
            var all = Enum.GetValues<LfoAssign>().Where(a => !(w == 1 && a is LfoAssign.Lfo1Rate or LfoAssign.Lfo1Depth) && !(w == 2 && a == LfoAssign.OscLevel)).ToArray();
            foreach (var a in all)
            {
                if (a == LfoAssign.Off) continue;
                l.Add(S($"Lfo{w}Assign Off->{a}", p => Base(p, LfoAssign.Off), e => e.SetParam(Pad, assign, (float)a)));
                l.Add(S($"Lfo{w}Assign {a}->Off", p => Base(p, a), e => e.SetParam(Pad, assign, (float)LfoAssign.Off)));
            }
            for (int i = 1; i < all.Length; i++)
            {
                var a = all[i]; var b = all[i % (all.Length - 1) + 1];
                l.Add(S($"Lfo{w}Assign {a}->{b}", p => Base(p, a), e => e.SetParam(Pad, assign, (float)b)));
            }
            // trig changes (note triggers happen later; the change itself must not move the output)
            foreach (var (a, b) in new[] { (0, 9), (9, 0), (0, 3), (9, 4), (3, 0) })
                l.Add(S($"Lfo{w}Trig {a}->{b} @Cutoff", p => Base(p, LfoAssign.Cutoff, LfoShape.Sine, a), e => e.SetParam(Pad, trig, b)));
            foreach (var (a, b) in new[] { (0, 9), (9, 0) })
                l.Add(S($"Lfo{w}Trig {a}->{b} @Level", p => Base(p, LfoAssign.Level, LfoShape.Sine, a), e => e.SetParam(Pad, trig, b)));
        }

        // ---- VOICE MODE (all transitions, notes held)
        foreach (VoiceMode a in Enum.GetValues<VoiceMode>())
            foreach (VoiceMode b in Enum.GetValues<VoiceMode>())
                if (a != b)
                    l.Add(P($"VoiceMode {a}->{b}", Pad, ParamId.VoiceMode, (float)a, (float)b));
        foreach (VoiceMode b in new[] { VoiceMode.Poly, VoiceMode.Mono, VoiceMode.Arp })
            l.Add(P($"VoiceMode {VoiceMode.Poly}->{b} (noise)", Noise, ParamId.VoiceMode, 0, (float)b));

        // ---- ARP / STEP NOTE / NOISE PLAY MODE
        foreach (ArpType b in new[] { ArpType.Down, ArpType.UpDown, ArpType.Random, ArpType.UpPlus1, ArpType.PlayOrder })
            l.Add(S($"ArpType Up->{b}", p => p.Set(Pad, ParamId.VoiceMode, (float)VoiceMode.Arp), e => e.SetParam(Pad, ParamId.ArpType, (float)b)));
        foreach (StepNote b in new[] { StepNote.Eighth1_8, StepNote.ThirtySecond1_32, StepNote.QuarterTriplet, StepNote.Quarter1_4 })
            l.Add(S($"StepNote 1/16->{b}", p => p.Set(Pad, ParamId.VoiceMode, (float)VoiceMode.Arp), e => e.SetParam(Pad, ParamId.StepNote, (float)b)));
        l.Add(P("NoisePlayMode Loop->Shot", Noise, ParamId.NoisePlayMode, 0, 1));
        l.Add(P("NoisePlayMode Shot->Loop", Noise, ParamId.NoisePlayMode, 1, 0));

        // ---- OCTAVE, tuning
        foreach (var (a, b) in new[] { (0, 1), (0, -1), (0, 2), (1, 0), (0, 3), (-1, 2) })
            l.Add(P($"Octave {a}->{b}", Pad, ParamId.Octave, a, b));
        l.Add(P("Octave 0->1 (noise)", Noise, ParamId.Octave, 0, 1));
        l.Add(P("Octave -1->0 (drone)", Drone, ParamId.Octave, -1, 0));
        foreach (var (a, b) in new[] { (Temperament.Equal, Temperament.PureMajor), (Temperament.PureMajor, Temperament.PureMinor), (Temperament.Equal, Temperament.PythagoreanSharp),
                     (Temperament.Equal, Temperament.Solfeggio), (Temperament.Equal, Temperament.Chakra), (Temperament.Solfeggio, Temperament.Equal) })
            l.Add(G($"Temperament {a}->{b}", GlobalParamId.Temperament, (float)a, (float)b));
        foreach (var (a, b) in new[] { (0, 1), (0, 5), (0, 7), (3, 9) })
            l.Add(S($"TuningKey {a}->{b}", p => { p.Set(GlobalParamId.Temperament, (float)Temperament.PureMajor); for (int k = 0; k < 4; k++) p.Set(k, ParamId.TuningKey, a); },
                e => { for (int k = 0; k < 4; k++) e.SetParam(k, ParamId.TuningKey, b); }));
        l.Add(G("MasterTune 0->+60c", GlobalParamId.MasterTuneCents, 0, 60));
        l.Add(G("TuneAll 0->+300c", GlobalParamId.TuneAllCents, 0, 300));

        // ---- reverb / FX / routing
        var rev = new[] { ReverbType.Off, ReverbType.SmallL, ReverbType.SmallM, ReverbType.SmallH, ReverbType.LargeL, ReverbType.LargeM, ReverbType.LargeH,
                          ReverbType.InfinityL, ReverbType.InfinityM, ReverbType.InfinityH };
        foreach (var b in rev)
            if (b != ReverbType.LargeM) l.Add(S($"ReverbType LargeM->{b}", p => p.Set(GlobalParamId.ReverbType, (float)ReverbType.LargeM), e => e.SetGlobal(GlobalParamId.ReverbType, (float)b)));
        foreach (var a in new[] { ReverbType.SmallL, ReverbType.SmallH, ReverbType.InfinityL, ReverbType.InfinityH })
            l.Add(S($"ReverbType {a}->LargeM", p => p.Set(GlobalParamId.ReverbType, (float)a), e => e.SetGlobal(GlobalParamId.ReverbType, (float)ReverbType.LargeM)));
        l.Add(S("ReverbType InfinityM->SmallM", p => p.Set(GlobalParamId.ReverbType, (float)ReverbType.InfinityM), e => e.SetGlobal(GlobalParamId.ReverbType, (float)ReverbType.SmallM)));
        l.Add(S("ReverbType SmallL->SmallH (same class)", p => p.Set(GlobalParamId.ReverbType, (float)ReverbType.SmallL), e => e.SetGlobal(GlobalParamId.ReverbType, (float)ReverbType.SmallH)));
        void FxSetup(ParameterStore p, MasterFxType t) { p.Set(GlobalParamId.FxType, (float)t); p.Set(GlobalParamId.FxAmount, 80); p.Set(GlobalParamId.FxSpeed, 70); }
        foreach (MasterFxType b in Enum.GetValues<MasterFxType>())
        {
            if (b == MasterFxType.Off) continue;
            l.Add(S($"FxType Off->{b}", p => { FxSetup(p, MasterFxType.Off); }, e => { e.SetGlobal(GlobalParamId.FxAmount, 80); e.SetGlobal(GlobalParamId.FxSpeed, 70); e.SetGlobal(GlobalParamId.FxType, (float)b); }));
            l.Add(S($"FxType {b}->Off", p => FxSetup(p, b), e => e.SetGlobal(GlobalParamId.FxType, 0)));
        }
        foreach (var (a, b) in new[] { (MasterFxType.TapeDelay, MasterFxType.Overdrive), (MasterFxType.Overdrive, MasterFxType.Chorus), (MasterFxType.Crush, MasterFxType.TiltEq),
                     (MasterFxType.ReverseDelay, MasterFxType.TapeDelay), (MasterFxType.Chorus, MasterFxType.Crush), (MasterFxType.TiltEq, MasterFxType.ReverseDelay) })
            l.Add(S($"FxType {a}->{b}", p => FxSetup(p, a), e => e.SetGlobal(GlobalParamId.FxType, (float)b)));
        l.Add(S("FxRouting POST->PRE", p => { FxSetup(p, MasterFxType.TapeDelay); p.Set(GlobalParamId.FxRouting, 0); }, e => e.SetGlobal(GlobalParamId.FxRouting, 1)));
        l.Add(S("FxRouting PRE->POST", p => { FxSetup(p, MasterFxType.TapeDelay); p.Set(GlobalParamId.FxRouting, 1); }, e => e.SetGlobal(GlobalParamId.FxRouting, 0)));
        l.Add(S("FxRouting POST->PRE (PatternLevel 40)", p => { FxSetup(p, MasterFxType.Overdrive); p.Set(GlobalParamId.PatternLevel, 40); }, e => e.SetGlobal(GlobalParamId.FxRouting, 1)));

        // ---- re-trigger of a sounding note with another velocity (ARP / MONO / sequencer steps do this all the time)
        l.Add(S("Retrigger same note vel 100->30 (pad)", null, e => e.NoteOn(Pad, 60, 30)));
        l.Add(S("Retrigger same note vel 100->127 (pad)", null, e => e.NoteOn(Pad, 64, 127)));
        l.Add(S("Retrigger same note vel 100->30 (noise)", null, e => e.NoteOn(Noise, 60, 30)));
        l.Add(S("Retrigger same note vel 100->30 (drone)", null, e => e.NoteOn(Drone, 43, 30)));

        // ---- mute / selected layer / misc
        l.Add(P("Mute off->on (pad)", Pad, ParamId.Mute, 0, 1));
        l.Add(P("Mute on->off (pad)", Pad, ParamId.Mute, 1, 0));
        l.Add(P("Mute off->on (noise)", Noise, ParamId.Mute, 0, 1));
        l.Add(G("SelectedLayer 2->3", GlobalParamId.SelectedLayer, 2, 3));
        l.Add(G("SelectedLayer 2->4", GlobalParamId.SelectedLayer, 2, 4));
        l.Add(G("SelectedLayer 1->2", GlobalParamId.SelectedLayer, 1, 2));

        // ---- continuous jumps (as on a palette load)
        void J(string n, ParamId id, float a, float b, int layer = Pad) => l.Add(P($"{n} {a}->{b}", layer, id, a, b));
        J("Cutoff", ParamId.Cutoff, 30, 120); J("Cutoff", ParamId.Cutoff, 120, 30); J("Cutoff", ParamId.Cutoff, 80, 127);
        J("Resonance", ParamId.Resonance, 20, 110); J("Resonance", ParamId.Resonance, 110, 20);
        J("Harmonic", ParamId.Harmonic, 10, 110); J("Harmonic", ParamId.Harmonic, 110, 5);
        J("Pitch", ParamId.Pitch, 0, 120); J("Pitch", ParamId.Pitch, 0, -240); J("Pitch", ParamId.Pitch, 100, -100);
        J("Detune", ParamId.Detune, 63.5f, 127); J("Detune", ParamId.Detune, 20, 110);
        J("Balance", ParamId.Balance, 10, 120); J("Balance", ParamId.Balance, 120, 10);
        J("Level", ParamId.Level, 63, 20); J("Level", ParamId.Level, 63, 127); J("Level", ParamId.Level, 20, 90);
        J("Pan", ParamId.Pan, -60, 60); J("Pan", ParamId.Pan, 60, -60);
        J("ReverbSend", ParamId.ReverbSend, 0, 127); J("ReverbSend", ParamId.ReverbSend, 127, 0);
        J("ShimmerSend", ParamId.ShimmerSend, 0, 127); J("ShimmerSend", ParamId.ShimmerSend, 100, 0);
        J("ModDepth", ParamId.ModDepth, 0, 127); J("ModDepth", ParamId.ModDepth, 127, 0);
        J("ModRate", ParamId.ModRate, 10, 120);
        J("Lfo1Depth", ParamId.Lfo1Depth, 0, 127); J("Lfo1Depth", ParamId.Lfo1Depth, 127, 0);
        J("Lfo1Rate", ParamId.Lfo1Rate, 10, 120);
        J("Lfo2Depth", ParamId.Lfo2Depth, 0, 127); J("Lfo2Rate", ParamId.Lfo2Rate, 10, 120);
        J("Attack", ParamId.Attack, 10, 100); J("Release", ParamId.Release, 10, 120);
        J("Cutoff", ParamId.Cutoff, 30, 120, Noise); J("Level", ParamId.Level, 20, 90, Noise); J("Harmonic", ParamId.Harmonic, 10, 120, Noise);
        J("Pan", ParamId.Pan, -60, 60, Noise); J("Level", ParamId.Level, 63, 20, Drone); J("Cutoff", ParamId.Cutoff, 30, 120, Drone);
        foreach (var tgt in new[] { LfoAssign.Level, LfoAssign.Pan, LfoAssign.Pitch, LfoAssign.Harmonic, LfoAssign.Tune, LfoAssign.Cutoff })
        {
            var t = tgt;
            void Lfo(ParameterStore p, float depth) { p.Set(Pad, ParamId.Lfo1Assign, (float)t); p.Set(Pad, ParamId.Lfo1Shape, (float)LfoShape.Sine); p.Set(Pad, ParamId.Lfo1Rate, 70); p.Set(Pad, ParamId.Lfo1Depth, depth); }
            l.Add(S($"Lfo1Depth 0->127 @{t}", p => Lfo(p, 0), e => e.SetParam(Pad, ParamId.Lfo1Depth, 127)));
            l.Add(S($"Lfo1Depth 127->0 @{t}", p => Lfo(p, 127), e => e.SetParam(Pad, ParamId.Lfo1Depth, 0)));
        }
        foreach (var (st, id, a, b) in new[] { (Structure.Atmos1, ParamId.ModDepth, 0f, 127f), (Structure.Atmos1, ParamId.ModDepth, 127f, 0f), (Structure.Atmos2, ParamId.Pitch, 0f, 120f),
                     (Structure.Atmos2, ParamId.Balance, 10f, 120f), (Structure.Atmos2, ParamId.Detune, 10f, 120f), (Structure.Atmos2, ParamId.ModDepth, 0f, 127f),
                     (Structure.Drone1, ParamId.ModDepth, 0f, 127f), (Structure.Pad1, ParamId.ModDepth, 127f, 0f) })
        {
            var (s2, i2, a2, b2) = (st, id, a, b);
            l.Add(S($"{s2} {i2} {a2}->{b2}", p => { p.Set(Pad, ParamId.Structure, (float)s2); p.Set(Pad, i2, a2); p.Set(Pad, ParamId.ModShape, 0); p.Set(Pad, ParamId.ModRate, 60); },
                e => e.SetParam(Pad, i2, b2)));
        }
        l.Add(S("Glide 5->120 (mono)", p => { p.Set(Pad, ParamId.VoiceMode, 1); p.Set(Pad, ParamId.Glide, 5); }, e => e.SetParam(Pad, ParamId.Glide, 120)));
        l.Add(S("UnisonDetune 10->120 (unison)", p => { p.Set(Pad, ParamId.VoiceMode, 3); p.Set(Pad, ParamId.UnisonDetune, 10); }, e => e.SetParam(Pad, ParamId.UnisonDetune, 120)));
        l.Add(G("MasterVolume 63->20", GlobalParamId.MasterVolume, 63.5f, 20));
        l.Add(G("MasterVolume 20->120", GlobalParamId.MasterVolume, 20, 120));
        l.Add(G("PatternLevel 63->10", GlobalParamId.PatternLevel, 63.5f, 10));
        l.Add(G("PatternLevel 10->120", GlobalParamId.PatternLevel, 10, 120));
        l.Add(G("ReverbMix 64->127", GlobalParamId.ReverbMix, 64, 127));
        l.Add(G("ReverbMix 127->0", GlobalParamId.ReverbMix, 127, 0));
        l.Add(G("FxAmount 0->127 (TapeDelay)", GlobalParamId.FxAmount, 0, 127, p => { p.Set(GlobalParamId.FxType, (float)MasterFxType.TapeDelay); }));
        l.Add(G("FxSpeed 20->110 (TapeDelay)", GlobalParamId.FxSpeed, 20, 110, p => { p.Set(GlobalParamId.FxType, (float)MasterFxType.TapeDelay); p.Set(GlobalParamId.FxAmount, 90); }));
        l.Add(G("FxAmount 0->127 (Overdrive)", GlobalParamId.FxAmount, 0, 127, p => { p.Set(GlobalParamId.FxType, (float)MasterFxType.Overdrive); }));
        l.Add(G("FxAmount 0->127 (Crush)", GlobalParamId.FxAmount, 0, 127, p => { p.Set(GlobalParamId.FxType, (float)MasterFxType.Crush); }));
        l.Add(G("FxSpeed 20->110 (Chorus)", GlobalParamId.FxSpeed, 20, 110, p => { p.Set(GlobalParamId.FxType, (float)MasterFxType.Chorus); p.Set(GlobalParamId.FxAmount, 90); }));
        return l;
    }

    // ------------------------------------------------------------------ palette switches

    /// <summary>
    /// Plays <paramref name="from"/> with the sequencer, then switches to <paramref name="to"/> (queued = at the cycle end,
    /// otherwise immediately) while the notes ring, and measures around the switch (the PaletteSwitch tap frame).
    /// </summary>
    public static ClickResult RunPalette(int fromSlot, int toSlot, bool queued, string? name = null)
    {
        var (bank, noise) = Banks();
        var p = new ParameterStore();
        var e = new AudioEngine(Fs, p, bank, noise);
        {
            var seq = new Sequencer(e, new PatternBank());
            long switchFrame = -1;
            seq.Tap = ev => { if (ev.Kind == SeqTapKind.PaletteSwitch && ev.Sample > 0 && switchFrame < 0) switchFrame = ev.Sample; };
            seq.LoadPaletteNow(0, fromSlot);
            var scratch = new float[Block * 2];
            var all = new List<float>(Fs * 2 * 30);
            void Render(int frames)
            {
                while (frames > 0)
                {
                    int n = Math.Min(Block, frames);
                    e.Render(scratch.AsSpan(0, n * 2), n);
                    for (int i = 0; i < n * 2; i++) all.Add(scratch[i]);
                    frames -= n; seq.Poll();
                }
            }
            Render(Block * 4);
            seq.Play();
            Render((int)(Fs * 9.0));     // let the sequence play: notes are ringing
            if (queued)
            {
                seq.SelectPalette(0, toSlot);
                for (int guard = 0; guard < 120 && switchFrame < 0; guard++) Render(Fs / 2);      // until the cycle boundary
            }
            else
            {
                seq.LoadPaletteNow(0, toSlot);
                Render(Block * 2);
            }
            if (switchFrame < 0) switchFrame = all.Count / 2 - Block * 2;
            Render((int)(Fs * 0.6));
            var arr = all.ToArray();
            if (Diag) Console.WriteLine("   " + Locate(arr, (int)switchFrame));
            return Measure(name ?? $"Palette {fromSlot}->{toSlot} {(queued ? "queued" : "immediate")}", arr, (int)switchFrame);
        }
    }

    /// <summary>
    /// Palette load with HELD keys instead of a playing sequence: the sequencer is stopped, notes are held on the four layers, then
    /// <paramref name="to"/> is loaded (every layer / global parameter jumps to the new palette's value while the notes ring). This
    /// isolates the parameter jumps and the reverb reset from the legitimate note onsets of a running sequence.
    /// </summary>
    public static ClickResult RunPaletteHeld(int fromSlot, int toSlot, string? name = null)
    {
        var (bank, noise) = Banks();
        var p = new ParameterStore();
        var e = new AudioEngine(Fs, p, bank, noise);
        var seq = new Sequencer(e, new PatternBank());
        if (NoReverbReset) seq.ResetReverbOnChange = false;
        long switchFrame = -1;
        seq.Tap = ev => { if (ev.Kind == SeqTapKind.PaletteSwitch && ev.Sample > 0 && switchFrame < 0) switchFrame = ev.Sample; };
        seq.LoadPaletteNow(fromSlot / 16, fromSlot % 16);
        var scratch = new float[Block * 2];
        var all = new List<float>(Fs * 2 * 6);
        void Render(int frames)
        {
            while (frames > 0)
            {
                int n = Math.Min(Block, frames);
                e.Render(scratch.AsSpan(0, n * 2), n);
                for (int i = 0; i < n * 2; i++) all.Add(scratch[i]);
                frames -= n; seq.Poll();
            }
        }
        Render(Block * 4);
        TextureNotes(e);
        Render((int)(Fs * 2.2));
        seq.LoadPaletteNow(toSlot / 16, toSlot % 16);
        Render(Block);
        if (switchFrame < 0) switchFrame = all.Count / 2 - Block;
        Render((int)(Fs * 0.5));
        var arr = all.ToArray();
        if (Diag) Console.WriteLine("   " + Locate(arr, (int)switchFrame));
        return Measure(name ?? $"Palette {fromSlot}->{toSlot} held notes", arr, (int)switchFrame);
    }

    /// <summary>Diagnostics: applies the parameter differences between two palettes ONE AT A TIME on top of the held-notes texture.</summary>
    public static void PaletteBisect(int fromSlot, int toSlot)
    {
        var (bank, noise) = Banks();
        var pb = new PatternBank();
        var a = pb.Load(fromSlot / 16, fromSlot % 16); var b = pb.Load(toSlot / 16, toSlot % 16);
        if (Diag) foreach (var (pal, tag) in new[] { (a, "FROM"), (b, "TO") })
            for (int l = 0; l < 4; l++)
                Console.WriteLine($"{tag} L{l + 1}: struct {pal.GetLayer(l, ParamId.Structure)} wave {pal.GetLayer(l, ParamId.Wave)} mode {pal.GetLayer(l, ParamId.VoiceMode)} arp {pal.GetLayer(l, ParamId.ArpType)} step {pal.GetLayer(l, ParamId.StepNote)} level {pal.GetLayer(l, ParamId.Level)} filt {pal.GetLayer(l, ParamId.FilterType)} oct {pal.GetLayer(l, ParamId.Octave)} lfo1 {pal.GetLayer(l, ParamId.Lfo1Assign)}/{pal.GetLayer(l, ParamId.Lfo1Depth)} lfo2 {pal.GetLayer(l, ParamId.Lfo2Assign)}/{pal.GetLayer(l, ParamId.Lfo2Depth)}");
        var changes = new List<(string, Action<ParameterStore>, Action<AudioEngine>)>();
        for (int l = 0; l < 4; l++)
            for (int k = 0; k < ParamTable.LayerParamCount; k++)
            {
                var id = (ParamId)k; int layer = l;
                float va = a.GetLayer(l, id), vb = b.GetLayer(l, id);
                if (va != vb) changes.Add(($"L{l + 1}.{id} {va:0.##}->{vb:0.##}", p => p.Set(layer, id, va), e => e.SetParam(layer, id, vb)));
            }
        foreach (var g in PatternPalette.PatternGlobals)
        {
            float va = a.GetGlobal(g), vb = b.GetGlobal(g);
            if (va != vb) changes.Add(($"{g} {va:0.##}->{vb:0.##}", p => p.Set(g, va), e => e.SetGlobal(g, vb)));
        }
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--loo") >= 0)
        {
            Action<ParameterStore> baseSetup = p =>
            {
                for (int l = 0; l < 4; l++) for (int k = 0; k < ParamTable.LayerParamCount; k++) p.Set(l, (ParamId)k, a.GetLayer(l, (ParamId)k));
                foreach (var g in PatternPalette.PatternGlobals) p.Set(g, a.GetGlobal(g));
                var ca = Environment.GetCommandLineArgs();
                int ri = Array.IndexOf(ca, "--rev"); if (ri >= 0) p.Set(GlobalParamId.ReverbType, int.Parse(ca[ri + 1]));
                int fi = Array.IndexOf(ca, "--fx"); if (fi >= 0) p.Set(GlobalParamId.FxType, int.Parse(ca[fi + 1]));
                int solo = Array.IndexOf(Environment.GetCommandLineArgs(), "--solo");
                if (solo >= 0) { int sl = int.Parse(Environment.GetCommandLineArgs()[solo + 1]); for (int l = 0; l < 4; l++) p.Set(l, ParamId.Mute, l == sl ? 0 : 1); }
            };
            int oi = Array.IndexOf(Environment.GetCommandLineArgs(), "--only");
            if (oi >= 0)
            {
                var keys = Environment.GetCommandLineArgs()[oi + 1].Split(',');
                var sel = changes.Where(c => keys.Any(k => c.Item1.StartsWith(k))).ToList();
                var rs = Run(new AuditScenario { Name = "ONLY", Setup = baseSetup, Change = e => { foreach (var c in sel) c.Item3(e); } }, true);
                Console.WriteLine($"ONLY {string.Join(" + ", sel.Select(c => c.Item1))}: {rs.BlockDb:F1} {rs.SettleDb:F1} {rs.RawSettleDb:F1}");
                if (Diag) Console.WriteLine("   " + LastLocate);
                return;
            }
            var rAll = Run(new AuditScenario { Name = "ALL", Setup = baseSetup, Change = e => { foreach (var c in changes) c.Item3(e); } }, true);
            Console.WriteLine($"ALL {rAll.BlockDb:F1} {rAll.SettleDb:F1} {rAll.RawSettleDb:F1}");
            for (int skip = 0; skip < changes.Count; skip++)
            {
                int sk = skip;
                var r = Run(new AuditScenario { Name = "x", Setup = baseSetup, Change = e => { for (int i = 0; i < changes.Count; i++) if (i != sk) changes[i].Item3(e); } }, true);
                Console.WriteLine($"without {changes[sk].Item1,-40} {r.BlockDb,8:F1} {r.SettleDb,8:F1}");
            }
            return;
        }
        foreach (var (n, setup, change) in changes)
        {
            Action<ParameterStore> full = p =>
            {
                for (int l = 0; l < 4; l++) for (int k = 0; k < ParamTable.LayerParamCount; k++) p.Set(l, (ParamId)k, a.GetLayer(l, (ParamId)k));
                foreach (var g in PatternPalette.PatternGlobals) p.Set(g, a.GetGlobal(g));
                setup(p);
            };
            var r = Run(new AuditScenario { Name = n, Setup = full, Change = change }, true);
            Console.WriteLine($"{n,-44} {r.BlockDb,8:F1} {r.SettleDb,8:F1} {r.RawSettleDb,8:F1} {(r.WorstDb > DefaultLimitDb ? "CLICK" : "")}");
            if (Diag && r.WorstDb > 3) Console.WriteLine("   " + LastLocate);
        }
    }

    public static List<(int From, int To)> PaletteHeldCases() => new()
    {
        (0, 1), (1, 0), (2, 5), (5, 2), (7, 3), (3, 7), (1, 9), (9, 1), (4, 0), (0, 4), (6, 8), (8, 6), (10, 12), (13, 15), (16, 33), (40, 20), (22, 50), (60, 3),
    };

    public static List<(int From, int To, bool Queued)> PaletteCases() => new()
    {
        (0, 1, false), (0, 1, true), (2, 5, false), (2, 5, true), (7, 3, false), (7, 3, true), (1, 9, false), (4, 0, true),
    };

    // ------------------------------------------------------------------ CLI

    public static int RunCli(string[] args)
    {
        Diag = args.Contains("--diag");
        int si = Array.IndexOf(args, "--solo2"); if (si >= 0) SoloLayer = int.Parse(args[si + 1]); NoReverbReset = args.Contains("--no-rst");
        int bi = Array.IndexOf(args, "--bisect");
        if (bi >= 0) { PaletteBisect(int.Parse(args[bi + 1]), int.Parse(args[bi + 2])); return 0; }
        bool reverbOnly = args.Contains("--reverb-only"), dry = !reverbOnly;
        string? filter = args.SkipWhile(a => a != "--filter").Skip(1).FirstOrDefault();
        bool flaggedOnly = args.Contains("--flagged");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Banks();
        Console.WriteLine($"banks ready in {sw.ElapsedMilliseconds} ms");
        int clicks = 0, total = 0;
        foreach (bool reverb in new[] { false, true })
        {
            if (reverb && !args.Contains("--with-reverb") && !reverbOnly) continue;
            if (!reverb && !dry) continue;
            Console.WriteLine($"\n=== {(reverb ? "reverb ON (LargeM)" : "reverb OFF, FX off")} ===");
            Console.WriteLine($"{"scenario",-52} {"spike near",10} {"spike 80ms",10} {"raw near",9} {"raw 80ms",9}  flag");
            foreach (var s in Scenarios())
            {
                if (filter != null && !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var r = Run(s, reverb);
                total++;
                bool click = r.WorstDb > s.LimitDb;
                if (click) clicks++;
                if (flaggedOnly && !click) continue;
                Console.WriteLine($"{s.Name,-52} {r.BlockDb,10:F1} {r.SettleDb,10:F1} {r.RawBlockDb,9:F1} {r.RawSettleDb,9:F1}  {(click ? "CLICK" : "")}");
                if (Diag && (click || !flaggedOnly)) Console.WriteLine("   " + LastLocate);
            }
        }
        if (args.Contains("--palettes"))
        {
            Console.WriteLine("\n=== palette loads with held notes (preset reverb / FX as designed) ===");
            foreach (var (f, t) in PaletteHeldCases())
            {
                var r = RunPaletteHeld(f, t);
                total++;
                bool click = r.WorstDb > DefaultLimitDb;
                if (click) clicks++;
                Console.WriteLine($"{r.Name,-52} {r.BlockDb,10:F1} {r.SettleDb,10:F1} {r.RawBlockDb,9:F1} {r.RawSettleDb,9:F1}  {(click ? "CLICK" : "")}");
            }
            Console.WriteLine("\n=== palette switches (preset reverb / FX as designed) ===");
            foreach (var (f, t, q) in PaletteCases())
            {
                var r = RunPalette(f, t, q);
                total++;
                bool click = r.WorstDb > DefaultLimitDb;
                if (click) clicks++;
                Console.WriteLine($"{r.Name,-52} {r.BlockDb,10:F1} {r.SettleDb,10:F1} {r.RawBlockDb,9:F1} {r.RawSettleDb,9:F1}  {(click ? "CLICK" : "")}");
            }
        }
        Console.WriteLine($"\n{total} measurements, {clicks} flagged (> +{DefaultLimitDb:F0} dB), {sw.Elapsed.TotalSeconds:F1} s");
        return 0;
    }
}
