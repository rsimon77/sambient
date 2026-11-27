using Sambient.Dsp.Filters;
using Sambient.Dsp.Modulation;
using Sambient.Dsp.Effects;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;
using Sambient.Tools;

namespace Sambient.Tests;

/// <summary>
/// Regression tests for audible clicks: every discrete / list parameter change, the big continuous jumps of a palette load and
/// palette switches are rendered offline on top of a sustained texture and the discontinuity around the change is compared with
/// the steady state before it (see <see cref="ClickAudit"/>; the same harness runs as <c>WaveDump --click-audit</c>).
/// </summary>
public class ClickAuditTests
{
    private static string Failures(IEnumerable<(AuditScenario s, ClickResult r)> results)
    {
        var bad = results.Where(x => x.r.WorstDb > x.s.LimitDb).Select(x => $"{x.s.Name}: near {x.r.BlockDb:F1} dB, 80 ms {x.r.SettleDb:F1} dB (limit {x.s.LimitDb:F0})").ToList();
        return string.Join("\n", bad);
    }

    private static List<(AuditScenario s, ClickResult r)> RunAll(IReadOnlyList<AuditScenario> scenarios, bool reverb)
    {
        ClickAudit.Banks();
        var res = new (AuditScenario, ClickResult)[scenarios.Count];
        Parallel.For(0, scenarios.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
            i => res[i] = (scenarios[i], ClickAudit.Run(scenarios[i], reverb)));
        return res.ToList();
    }

    [Fact]
    public void EveryParameterChange_IsClickFree_ReverbAndFxOff()
    {
        var scenarios = ClickAudit.Scenarios();
        Assert.True(scenarios.Count > 300, $"only {scenarios.Count} scenarios");
        string bad = Failures(RunAll(scenarios, reverb: false));
        Assert.True(bad.Length == 0, "clicks:\n" + bad);
    }

    [Fact]
    public void ParameterChanges_AreClickFree_WithReverbOn()
    {
        // every 3rd scenario plus all reverb / FX / routing ones (the reverb tail makes the signal denser; the reverb itself is under test)
        var all = ClickAudit.Scenarios();
        var subset = all.Where((s, i) => i % 3 == 0 || s.Name.StartsWith("Reverb") || s.Name.StartsWith("Fx") || s.Name.StartsWith("Palette")).ToList();
        string bad = Failures(RunAll(subset, reverb: true));
        Assert.True(bad.Length == 0, "clicks:\n" + bad);
    }

    [Fact]
    public void PaletteLoad_WithHeldNotes_IsClickFree()
    {
        ClickAudit.Banks();
        var cases = ClickAudit.PaletteHeldCases();
        var res = new ClickResult[cases.Count];
        Parallel.For(0, cases.Count, new ParallelOptions { MaxDegreeOfParallelism = 3 }, i => res[i] = ClickAudit.RunPaletteHeld(cases[i].From, cases[i].To));
        var bad = res.Where(r => r.WorstDb > ClickAudit.DefaultLimitDb).Select(r => $"{r.Name}: near {r.BlockDb:F1} dB, 80 ms {r.SettleDb:F1} dB").ToList();
        Assert.True(bad.Count == 0, "clicks:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void PaletteSwitch_WhileTheSequenceRings_QueuedAndImmediate()
    {
        // These renders contain the legitimate note onsets of the new sequence right at the switch (sometimes in a very quiet
        // passage), so the limit is looser than for a plain parameter change: +12 dB instead of +6 dB.
        ClickAudit.Banks();
        var cases = ClickAudit.PaletteCases();
        var res = new ClickResult[cases.Count];
        Parallel.For(0, cases.Count, new ParallelOptions { MaxDegreeOfParallelism = 3 }, i => res[i] = ClickAudit.RunPalette(cases[i].From, cases[i].To, cases[i].Queued));
        var bad = res.Where(r => r.WorstDb > 12.0).Select(r => $"{r.Name}: near {r.BlockDb:F1} dB, 80 ms {r.SettleDb:F1} dB").ToList();
        Assert.True(bad.Count == 0, "clicks:\n" + string.Join("\n", bad));
    }

    // ------------------------------------------------------------------ the harness itself

    [Fact]
    public void Harness_DetectsAnInjectedStep_AndStaysQuietWithoutAChange()
    {
        var (bank, noise) = ClickAudit.Banks();
        var p = new ParameterStore();
        ClickAudit.TextureParams(p, reverb: false);
        var e = new AudioEngine(ClickAudit.Fs, p, bank, noise);
        ClickAudit.TextureNotes(e);
        int frames = (int)(2.1 * ClickAudit.Fs);
        var buf = new float[frames * 2];
        for (int pos = 0; pos < frames; pos += ClickAudit.Block) e.Render(buf.AsSpan(pos * 2, Math.Min(ClickAudit.Block, frames - pos) * 2), Math.Min(ClickAudit.Block, frames - pos));
        int change = (int)(1.6 * ClickAudit.Fs);
        var quiet = ClickAudit.Measure("control", buf, change);
        Assert.True(quiet.WorstDb < 3.0, $"control {quiet.WorstDb:F1} dB");
        // a hard DC step of 0.05 from the change frame on (what an un-ramped gain / filter switch looks like)
        for (int i = change; i < frames; i++) { buf[2 * i] += 0.05f; buf[2 * i + 1] += 0.05f; }
        var step = ClickAudit.Measure("step", buf, change);
        Assert.True(step.WorstDb > ClickAudit.DefaultLimitDb + 2, $"injected step only {step.WorstDb:F1} dB");
    }

    // ------------------------------------------------------------------ properties

    [Fact]
    public void SelectedLayerChange_IsBitIdentical()
    {
        var (bank, noise) = ClickAudit.Banks();
        float[] Render(bool change)
        {
            var p = new ParameterStore();
            ClickAudit.TextureParams(p, reverb: true);
            var e = new AudioEngine(ClickAudit.Fs, p, bank, noise);
            ClickAudit.TextureNotes(e);
            var buf = new float[ClickAudit.Fs * 2];
            int frames = ClickAudit.Fs;
            for (int pos = 0; pos < frames; pos += ClickAudit.Block)
            {
                if (change && pos == 8 * ClickAudit.Block) e.SetGlobal(GlobalParamId.SelectedLayer, 3);
                if (change && pos == 14 * ClickAudit.Block) e.SetGlobal(GlobalParamId.SelectedLayer, 4);
                if (change && pos == 20 * ClickAudit.Block) e.SetGlobal(GlobalParamId.SelectedLayer, 2);
                int n = Math.Min(ClickAudit.Block, frames - pos);
                e.Render(buf.AsSpan(pos * 2, n * 2), n);
            }
            return buf;
        }
        var a = Render(false); var b = Render(true);
        for (int i = 0; i < a.Length; i++)
            Assert.True(BitConverter.SingleToInt32Bits(a[i]) == BitConverter.SingleToInt32Bits(b[i]), $"sample {i} differs: {a[i]} vs {b[i]}");
    }

    [Fact]
    public void WarmUp_DoesNotTouchALiveEngine()
    {
        var (bank, noise) = ClickAudit.Banks();
        ParameterStore MakeStore()
        {
            var p = new ParameterStore();
            ClickAudit.TextureParams(p, reverb: true);
            p.Set(1, ParamId.Structure, (float)Structure.Pad2); p.Set(1, ParamId.Cutoff, 55); p.Set(0, ParamId.Level, 41);
            p.Set(GlobalParamId.ReverbType, (float)ReverbType.SmallH); p.Set(GlobalParamId.FxType, (float)MasterFxType.Chorus);
            p.Set(GlobalParamId.Bpm, 133); p.Set(GlobalParamId.SelectedLayer, 3);
            return p;
        }
        float[] Snapshot(ParameterStore p)
        {
            var l = new List<float>();
            for (int layer = 0; layer < ParameterStore.MaxLayers; layer++)
                for (int k = 0; k < ParamTable.LayerParamCount; k++) { l.Add(p.GetBase(layer, (ParamId)k)); l.Add(p.HasOverride(layer, (ParamId)k) ? 1 : 0); }
            for (int k = 0; k < ParamTable.GlobalParamCount; k++) l.Add(p.Get((GlobalParamId)k));
            return l.ToArray();
        }
        float[] RenderLive(ParameterStore p, bool warmUpInBetween)
        {
            var e = new AudioEngine(ClickAudit.Fs, p, bank, noise);
            ClickAudit.TextureNotes(e);
            var buf = new float[ClickAudit.Block * 2 * 40];
            for (int i = 0; i < 20; i++) e.Render(buf.AsSpan(i * ClickAudit.Block * 2, ClickAudit.Block * 2), ClickAudit.Block);
            if (warmUpInBetween) EngineWarmUp.WarmUp(bank, noise, ClickAudit.Fs);
            for (int i = 20; i < 40; i++) e.Render(buf.AsSpan(i * ClickAudit.Block * 2, ClickAudit.Block * 2), ClickAudit.Block);
            Assert.Equal(0, e.DroppedEvents);
            return buf;
        }
        var pa = MakeStore(); var before = Snapshot(pa);
        var plain = RenderLive(MakeStore(), false);
        var warmed = RenderLive(pa, true);
        Assert.Equal(before, Snapshot(pa));                       // the live parameter store is untouched
        for (int i = 0; i < plain.Length; i++)                     // and the live engine renders exactly the same
            Assert.True(BitConverter.SingleToInt32Bits(plain[i]) == BitConverter.SingleToInt32Bits(warmed[i]), $"sample {i} differs");
    }

    [Fact]
    public void WarmUp_OnAFreshBank_BuildsOnlySinSAndNoiseSlot1()
    {
        var bank = new WavetableBank(); var noise = new Sambient.Dsp.Noise.NoiseBank();
        EngineWarmUp.WarmUp(bank, noise, 48000);
        Assert.True(bank.IsReady(WavetableBank.FallbackIndex));
        Assert.False(bank.IsReady(8));
        Assert.False(bank.IsReady(20));
        Assert.True(noise.IsReady(1));
        Assert.False(noise.IsReady(5));
    }

    [Fact]
    public void Render_WithParameterChanges_DoesNotAllocate_AfterWarmUp()
    {
        var (bank, noise) = ClickAudit.Banks();
        EngineWarmUp.WarmUp(bank, noise, ClickAudit.Fs);
        var p = new ParameterStore();
        ClickAudit.TextureParams(p, reverb: true);
        var e = new AudioEngine(ClickAudit.Fs, p, bank, noise);
        ClickAudit.TextureNotes(e);
        var buf = new float[ClickAudit.Block * 2];
        for (int i = 0; i < 40; i++) e.Render(buf, ClickAudit.Block);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            switch (i % 10)
            {
                case 0: e.SetParam(1, ParamId.FilterType, (i / 10) % 4); break;
                case 1: e.SetParam(1, ParamId.Lfo1Assign, (i / 10) % 14 + 1); e.SetParam(1, ParamId.Lfo1Depth, 80); break;
                case 2: e.SetParam(1, ParamId.Lfo2Shape, (i / 10) % 24); break;
                case 3: e.SetGlobal(GlobalParamId.FxRouting, (i / 10) & 1); break;
                case 4: e.SetGlobal(GlobalParamId.ReverbType, (i / 10) % 10); break;
                case 5: e.SetGlobal(GlobalParamId.FxType, (i / 10) % 7); e.SetGlobal(GlobalParamId.FxAmount, 70); break;
                case 6: e.SetGlobal(GlobalParamId.Temperament, (i / 10) % 8); break;
                case 7: e.SetParam(1, ParamId.VoiceMode, (i / 10) % 5); break;
                case 8: e.SetParam(1, ParamId.Octave, (i / 10) % 3 - 1); e.NoteOn(1, 60, 20 + i % 100); break;
                case 9: e.SetParam(1, ParamId.Level, 10 + (i * 7) % 100); break;
            }
            e.Render(buf, ClickAudit.Block);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ------------------------------------------------------------------ the building blocks

    [Fact]
    public void Svf_ModeChange_CrossfadesInsteadOfStepping()
    {
        const float fs = 48000f;
        var f = new StateVariableFilter(fs, FilterMode.LowPass);
        f.SetParameters(200f, 0.707f);
        float prev = 0f; for (int i = 0; i < 4800; i++) prev = f.Process(1f);       // settled low-pass of a DC input: ~1
        Assert.InRange(prev, 0.99f, 1.01f);
        f.Mode = FilterMode.HighPass;                                                // a DC input through a high-pass: ~0
        float maxStep = 0f;
        for (int i = 0; i < 4800; i++) { float y = f.Process(1f); maxStep = Math.Max(maxStep, MathF.Abs(y - prev)); prev = y; }
        Assert.True(maxStep < 0.01f, $"largest per-sample step {maxStep}");
        Assert.InRange(MathF.Abs(prev), 0f, 0.02f);
    }

    [Fact]
    public void Svf_FromOff_StartsFromRest_AndNewInstanceNeverFades()
    {
        const float fs = 48000f;
        var f = new StateVariableFilter(fs, FilterMode.Off);
        f.SetParameters(500f, 0.707f);
        Assert.Equal(0.3f, f.Process(0.3f));                                          // OFF stays an exact passthrough
        f.Mode = FilterMode.LowPass;
        float first = f.Process(0.3f);
        Assert.InRange(first, 0.25f, 0.31f);                                          // mostly the old (dry) tap at the start of the fade
        var g = new StateVariableFilter(fs, FilterMode.HighPass);
        g.SetModeImmediate(FilterMode.LowPass);
        g.SetParameters(500f, 0.707f);
        Assert.True(g.Process(1f) < 0.05f);                                           // immediate: no crossfade tap of the previous type
    }

    [Fact]
    public void Lfo_ShapeChange_IsMorphedWhenEnabled_AndHardOtherwise()
    {
        const float fs = 48000f;
        float MaxStep(bool morph)
        {
            var l = new Lfo(fs, 1) { MorphShapeChanges = morph };
            l.Configure(LfoWave.Square, 5f, Lfo.TrigOff);
            float prev = 0f; for (int i = 0; i < 2000; i++) prev = l.Next();           // inside the +1 half of the square
            l.Configure(LfoWave.Triangle, 5f, Lfo.TrigOff);
            float max = 0f; for (int i = 0; i < 2000; i++) { float y = l.Next(); max = Math.Max(max, MathF.Abs(y - prev)); prev = y; }
            return max;
        }
        Assert.True(MaxStep(true) < 0.01f);
        Assert.True(MaxStep(false) > 0.1f);
    }

    [Fact]
    public void MasterFx_SwitchToDelayEffects_FadesTheEchoInSmoothly()
    {
        const int fs = 48000;
        foreach (var type in new[] { MasterFxType.TapeDelay, MasterFxType.ReverseDelay, MasterFxType.Chorus })
        {
            var fx = new MasterFx(fs);
            fx.SetType(MasterFxType.Off);
            fx.SetType(type); fx.SetSpeed(40); fx.SetAmount(100);
            int n = fs / 2;
            var l = new float[n]; var r = new float[n];
            for (int i = 0; i < n; i++) l[i] = r[i] = 0.4f * MathF.Sin(2 * MathF.PI * 440f * i / fs);
            var dry = (float[])l.Clone();
            fx.Process(l, r);
            // the wet part is the difference to the dry signal: it must not start with a step (second difference of the wet part stays small)
            double maxD2 = 0;
            for (int i = 2; i < n; i++)
            {
                double w0 = l[i] - dry[i], w1 = l[i - 1] - dry[i - 1], w2 = l[i - 2] - dry[i - 2];
                maxD2 = Math.Max(maxD2, Math.Abs(w0 - 2 * w1 + w2));
            }
            Assert.True(maxD2 < 0.03, $"{type}: wet second difference {maxD2}");
        }
    }
}
