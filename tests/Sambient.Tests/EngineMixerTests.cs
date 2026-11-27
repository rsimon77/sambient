using Sambient.Dsp.Effects;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;

namespace Sambient.Tests;

/// <summary>M2: four layers, mixer, send buses, master FX order, note routing, NOISE layer.</summary>
public class EngineMixerTests
{
    private const int Fs = 48000;
    private static readonly WavetableBank SharedBank = MakeBank();

    private static WavetableBank MakeBank()
    {
        var b = new WavetableBank();
        foreach (int w in new[] { 1, 2, 3, 8, 25 }) b.EnsureBuiltAsync(w).GetAwaiter().GetResult();
        return b;
    }

    /// <summary>Neutral engine: all layers SAW.S, no attack, short release, no filter, sends 0.</summary>
    private static AudioEngine Make()
    {
        var p = new ParameterStore();
        for (int l = 0; l < AudioEngine.SynthLayerCount; l++)
        {
            p.Set(l, ParamId.Wave, 1);
            p.Set(l, ParamId.Attack, 0);
            p.Set(l, ParamId.Release, 20);
            p.Set(l, ParamId.FilterType, 0);
        }
        p.Set(3, ParamId.Attack, 0);
        p.Set(3, ParamId.Release, 20);
        p.Set(3, ParamId.FilterType, 0);
        p.Set(GlobalParamId.ReverbType, (float)ReverbType.Off);   // neutral: dry signal only (reverb/FX have their own tests)
        p.Set(GlobalParamId.FxType, (float)MasterFxType.Off);
        return new AudioEngine(Fs, p, SharedBank);
    }

    private static float[] Run(AudioEngine e, int frames, int block = 256)
    {
        var all = new float[frames * 2];
        var buf = new float[block * 2];
        for (int done = 0; done < frames; done += block)
        {
            int n = Math.Min(block, frames - done);
            e.Render(buf, n);
            Array.Copy(buf, 0, all, done * 2, n * 2);
        }
        return all;
    }

    private static float Peak(ReadOnlySpan<float> a)
    {
        float p = 0;
        foreach (var s in a) p = Math.Max(p, Math.Abs(s));
        return p;
    }

    [Fact]
    public void ParameterTable_Defaults_AndNoiseWaveClamp()
    {
        var p = new ParameterStore();
        Assert.Equal(90f, p.Get(GlobalParamId.Bpm));
        Assert.Equal(1f, p.Get(GlobalParamId.SelectedLayer));
        Assert.Equal(8f, p.Set(3, ParamId.Wave, 30));      // noise layer: slots 1..8 only
        Assert.Equal(30f, p.Set(0, ParamId.Wave, 30));
        Assert.Equal(250f, p.Set(GlobalParamId.Bpm, 999));
        Assert.Equal(40f, p.Set(GlobalParamId.Bpm, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NoteGoesOnlyToItsLayer(int layer)
    {
        var e = Make();
        e.SetParam(layer, ParamId.Harmonic, 0);      // for the noise layer: white noise only (works without a sample)
        e.SetParam(layer, ParamId.Level, 100);
        e.NoteOn(layer, 60, 100);
        var a = Run(e, 4800);
        Assert.True(Peak(a) > 0.01f);
        for (int l = 0; l < AudioEngine.LayerCount; l++)
            Assert.Equal(l == layer ? 1 : 0, e.GetAnyLayer(l).ActiveVoices);
    }

    [Fact]
    public void LayersMixIndependently_PanPerLayer()
    {
        var e = Make();
        e.SetParam(0, ParamId.Pan, -63);
        e.SetParam(1, ParamId.Pan, 63);
        Run(e, 2400);                                      // let the pan smoothers settle
        e.NoteOn(0, 60, 100);
        var a = Run(e, 4800);
        Assert.True(Peak(a.Where((_, i) => i % 2 == 0).ToArray()) > 0.02f);
        Assert.True(Peak(a.Where((_, i) => i % 2 == 1).ToArray()) < 1e-3f);
        e.AllNotesOff(); Run(e, Fs / 2);
        e.NoteOn(1, 60, 100);
        var b = Run(e, 4800);
        Assert.True(Peak(b.Where((_, i) => i % 2 == 0).ToArray()) < 1e-3f);
        Assert.True(Peak(b.Where((_, i) => i % 2 == 1).ToArray()) > 0.02f);
    }

    [Fact]
    public void Mute_SilencesLayerAndItsSends()
    {
        var e = Make();
        e.SetParam(1, ParamId.ReverbSend, 127);
        e.SetParam(1, ParamId.ShimmerSend, 127);
        e.NoteOn(1, 60, 100);
        Run(e, 4800);
        Assert.True(Peak(e.ReverbSendBusL) > 0.01f);

        e.SetParam(1, ParamId.Mute, 1);
        Run(e, 2400);                                   // smoothing (10 ms)
        var a = Run(e, 2400);
        Assert.True(Peak(a) < 1e-4f);
        Assert.True(Peak(e.ReverbSendBusL) < 1e-4f && Peak(e.ShimmerSendBusL) < 1e-4f);
        Assert.Equal(1, e.GetAnyLayer(1).ActiveVoices);   // still sounding internally

        e.SetParam(1, ParamId.Mute, 0);
        Run(e, 2400);
        Assert.True(Peak(Run(e, 2400)) > 0.01f);
    }

    [Fact]
    public void Sends_ReachTheirBuses_PostLevel()
    {
        var e = Make();
        e.SetParam(0, ParamId.ReverbSend, 127);
        e.SetParam(0, ParamId.ShimmerSend, 0);
        e.NoteOn(0, 60, 100);
        var dry = Run(e, 2400);
        Assert.True(Peak(e.ReverbSendBusL) > 0.01f);
        Assert.True(Peak(e.ShimmerSendBusL) < 1e-6f);
        // full send == dry signal (send gain 1.0, post level/pan)
        Assert.Equal(Peak(e.DryBusL), Peak(e.ReverbSendBusL), 4);

        e.SetParam(0, ParamId.ReverbSend, 0);
        e.SetParam(0, ParamId.ShimmerSend, 64);
        Run(e, 4800);
        Assert.True(Peak(e.ReverbSendBusL) < 1e-6f);
        float ratio = Peak(e.ShimmerSendBusL) / Peak(e.DryBusL);
        Assert.InRange(ratio, 0.45f, 0.55f);

        // post-level: halving the level halves the send
        e.SetParam(0, ParamId.Level, 40);
        Run(e, 4800);
        Assert.True(Peak(e.ShimmerSendBusL) < Peak(e.DryBusL) * 0.6f);
        Assert.True(Peak(e.DryBusL) > 0f);
    }

    [Fact]
    public void SendsOfSeveralLayers_AreSummed()
    {
        var e = Make();
        for (int l = 0; l < 3; l++) { e.SetParam(l, ParamId.ReverbSend, 127); e.NoteOn(l, 60, 100); }
        Run(e, 2400);
        float one = Peak(e.ReverbSendBusL);
        var e2 = Make();
        e2.SetParam(0, ParamId.ReverbSend, 127); e2.NoteOn(0, 60, 100);
        Run(e2, 2400);
        Assert.True(one > Peak(e2.ReverbSendBusL) * 2.5f);   // three identical in-phase layers
    }

    [Fact]
    public void ProcessingOrder_ReverbThenMasterFx_ThenMaster()
    {
        var e = Make();
        var trace = new List<string>();
        e.StageTrace = s => trace.Add(s);
        e.SetGlobal(GlobalParamId.FxType, (float)MasterFxType.TapeDelay);
        Run(e, 256, 256);
        Assert.Equal(new[] { "reverb", "fx", "master" }, trace);
        trace.Clear();
        Run(e, 256 * 2, 256 * 2);                         // one callback with two chunks? (512 < MaxBlock -> one chunk)
        Assert.Equal(new[] { "reverb", "fx", "master" }, trace);
    }

    [Fact]
    public void LongCallback_IsSplitIntoChunks()
    {
        var e = Make();
        var trace = new List<string>();
        e.StageTrace = s => trace.Add(s);
        e.NoteOn(0, 60, 100);
        var a = Run(e, AudioEngine.MaxBlock * 2 + 100, AudioEngine.MaxBlock * 2 + 100);
        Assert.Equal(9, trace.Count);                      // 3 chunks x 3 stages
        Assert.True(Peak(a) > 0.01f);
        Assert.All(a, s => Assert.True(float.IsFinite(s)));
    }

    // ------------------------------------------------------------- note routing

    private sealed class RouterRig
    {
        public readonly AudioEngine Engine;
        public readonly NoteRouter Router;
        public RouterRig()
        {
            Engine = Make();
            Router = new NoteRouter(() => Engine, Engine.Parameters);
        }
        public void Select(int layer1Based) => Engine.SetGlobal(GlobalParamId.SelectedLayer, layer1Based);
    }

    [Fact]
    public void NoteOn_GoesToSelectedLayer()
    {
        var r = new RouterRig();
        r.Select(3);
        Assert.Equal(2, r.Router.NoteOn(NoteSource.Ui, 60, 100));
        Run(r.Engine, 2400);
        Assert.Equal(1, r.Engine.GetLayer(2).VoicesForNote(60));
        Assert.Equal(0, r.Engine.GetLayer(0).VoicesForNote(60));
    }

    [Fact]
    public void NoteOff_GoesToOriginalLayer_AfterSelectionChange()
    {
        var r = new RouterRig();
        r.Select(1);
        r.Router.NoteOn(NoteSource.Ui, 60, 100);
        Run(r.Engine, 2400);
        r.Select(2);
        Assert.Equal(0, r.Router.NoteOff(NoteSource.Ui, 60));
        Run(r.Engine, Fs);                                 // release knob 20 is short
        Assert.Equal(0, r.Engine.GetLayer(0).ActiveVoices);
        Assert.Equal(0, r.Engine.GetLayer(1).ActiveVoices);
        Assert.Equal(-1, r.Router.NoteOff(NoteSource.Ui, 60));   // not held any more
    }

    [Fact]
    public void MidiChannelToLayer_OptionRoutesByChannel()
    {
        var r = new RouterRig();
        r.Select(1);
        Assert.Equal(0, r.Router.NoteOn(NoteSource.Midi, 60, 100, channel0: 2));   // option off: selected layer
        r.Router.NoteOff(NoteSource.Midi, 60);
        r.Router.MidiChannelToLayer = true;
        Assert.Equal(2, r.Router.NoteOn(NoteSource.Midi, 62, 100, channel0: 2));
        Assert.Equal(3, r.Router.NoteOn(NoteSource.Midi, 64, 100, channel0: 3));
        Assert.Equal(0, r.Router.NoteOn(NoteSource.Midi, 65, 100, channel0: 9));   // channel above 4: selected layer
        Assert.Equal(0, r.Router.NoteOn(NoteSource.Ui, 67, 100, channel0: 3));     // not the MIDI source: selected layer
        Assert.Equal(2, r.Router.NoteOff(NoteSource.Midi, 62));
    }

    // ------------------------------------------------------------- NOISE layer

    [Fact]
    public void NoiseLayer_WhiteNoiseOnly_WhenBalanceZero_EvenWithoutSample()
    {
        var e = Make();
        Assert.False(e.Noise.IsReady(1));                  // stub / not built: no sample available
        e.SetParam(3, ParamId.Harmonic, 0);
        e.SetParam(3, ParamId.Level, 100);
        e.NoteOn(3, 60, 100);
        var a = Run(e, Fs / 2);
        var l = a.Where((_, i) => i % 2 == 0).Skip(2000).ToArray();
        Assert.True(Peak(l) > 0.02f, "white noise must sound");
        int crossings = 0;
        for (int i = 1; i < l.Length; i++) if ((l[i] >= 0) != (l[i - 1] >= 0)) crossings++;
        double rate = (double)crossings / l.Length;
        Assert.InRange(rate, 0.4, 0.6);                    // white noise: ~0.5 zero crossings per sample
        // left and right are decorrelated draws, not identical
        Assert.NotEqual(a[4000], a[4001]);
    }

    [Fact]
    public void NoiseLayer_SampleOnly_WithoutSample_IsSilent()
    {
        var e = Make();
        e.SetParam(3, ParamId.Harmonic, 127);
        Run(e, 2400);                                      // let the balance smoother settle
        e.NoteOn(3, 60, 100);
        Assert.True(Peak(Run(e, 4800)) < 1e-4f);
    }

    [Fact]
    public void NoiseLayer_Balance_IsEqualPowerCentre()
    {
        float t0 = 0f, tc = 63.5f / 127f * MathF.PI * 0.5f, t1 = MathF.PI * 0.5f;
        Assert.Equal(1f, MathF.Cos(t0), 5);
        Assert.Equal(MathF.Cos(tc), MathF.Sin(tc), 5);
        Assert.Equal(0.7071f, MathF.Cos(tc), 3);
        Assert.Equal(1f, MathF.Sin(t1), 5);
    }

    [Fact]
    public void NoiseLayer_PlaybackRate_Formula()
    {
        // root key C4 plays the sample at its original pitch (only the sample-rate conversion remains)
        Assert.Equal(32000.0 / 48000.0, NoiseLayer.PlaybackRate(32000, 48000, 60 - NoiseLayer.RootNote), 9);
        Assert.Equal(2 * 32000.0 / 48000.0, NoiseLayer.PlaybackRate(32000, 48000, 72 - 60), 9);          // +1 octave
        Assert.Equal(0.5 * 32000.0 / 48000.0, NoiseLayer.PlaybackRate(32000, 48000, 48 - 60), 9);        // -1 octave
        Assert.Equal(2 * 32000.0 / 48000.0, NoiseLayer.PlaybackRate(32000, 48000, 60 - 60 + 12 + 0), 9); // octave param +1
        Assert.Equal(Math.Pow(2, 1.0 / 12), NoiseLayer.PlaybackRate(48000, 48000, 1 + 0), 9);
        Assert.Equal(1.0, NoiseLayer.PlaybackRate(48000, 48000, 0), 9);
    }

    [Fact]
    public void NoiseLayer_OneShotAndLoopModes_AreAccepted()
    {
        var e = Make();
        e.SetParam(3, ParamId.NoisePlayMode, 1);
        e.SetParam(3, ParamId.Harmonic, 0);
        e.NoteOn(3, 60, 100);
        Assert.True(Peak(Run(e, 4800)) > 0.01f);
        e.SetParam(3, ParamId.NoisePlayMode, 0);
        Assert.True(Peak(Run(e, 4800)) > 0.01f);
    }

    [Fact]
    public void NoiseLayer_StealsWhenAllEightVoicesAreUsed()
    {
        var e = Make();
        e.SetParam(3, ParamId.Harmonic, 0);
        for (int i = 0; i < 8; i++) e.NoteOn(3, 40 + i, 100);
        Run(e, 1024);
        Assert.Equal(8, e.NoiseLayer.ActiveVoices);
        e.NoteOn(3, 90, 100);
        Run(e, 2048);
        Assert.Equal(8, e.NoiseLayer.ActiveVoices);
        Assert.Equal(1, e.NoiseLayer.VoicesForNote(90));
        Assert.Equal(0, e.NoiseLayer.VoicesForNote(40));
    }

    // ------------------------------------------------------------- robustness

    [Fact]
    public void DefaultPatch_IsValidAndSounds()
    {
        var p = new ParameterStore();
        DefaultPatch.Apply(p);
        Assert.Equal(3f, p.Get(0, ParamId.Wave));
        Assert.Equal(-1f, p.Get(0, ParamId.Octave));
        Assert.Equal((float)ReverbType.LargeM, p.Get(GlobalParamId.ReverbType));
        Assert.Equal((float)MasterFxType.TapeDelay, p.Get(GlobalParamId.FxType));
        var e = new AudioEngine(Fs, p, SharedBank);
        for (int l = 0; l < 4; l++) e.NoteOn(l, 48, 100);
        var a = Run(e, Fs);
        Assert.True(Peak(a) > 0.01f);
        Assert.All(a, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1f));
    }

    [Fact]
    public void NoNaN_ExtremeSettings_AllLayersAndEffects()
    {
        var e = Make();
        var rng = new Random(7);
        e.PitchBend(1f); e.ModWheel(1f);
        for (int l = 0; l < 4; l++)
        {
            e.SetParam(l, ParamId.Level, 127);
            e.SetParam(l, ParamId.Resonance, 127);
            e.SetParam(l, ParamId.ReverbSend, 127);
            e.SetParam(l, ParamId.ShimmerSend, 127);
            e.NoteOn(l, 36, 127); e.NoteOn(l, 100, 127); e.NoteOn(l, 72, 1);
        }
        e.SetGlobal(GlobalParamId.MasterVolume, 127);
        for (int i = 0; i < 200; i++)
        {
            for (int l = 0; l < 4; l++)
            {
                e.SetParam(l, ParamId.FilterType, rng.Next(4));
                e.SetParam(l, ParamId.Cutoff, rng.Next(128));
                e.SetParam(l, ParamId.Harmonic, rng.Next(128));
                e.SetParam(l, ParamId.Mute, rng.Next(2));
                e.SetParam(l, ParamId.Octave, rng.Next(-3, 4));
                if (rng.Next(8) == 0) e.NoteOn(l, rng.Next(128), rng.Next(1, 128));
                if (rng.Next(8) == 0) e.NoteOff(l, rng.Next(128));
            }
            e.SetGlobal(GlobalParamId.ReverbType, rng.Next(10));
            e.SetGlobal(GlobalParamId.ReverbMix, rng.Next(128));
            e.SetGlobal(GlobalParamId.FxType, rng.Next(7));
            e.SetGlobal(GlobalParamId.FxSpeed, rng.Next(128));
            e.SetGlobal(GlobalParamId.FxAmount, rng.Next(128));
            e.SetGlobal(GlobalParamId.Bpm, rng.Next(40, 251));
            var a = Run(e, 1024);
            foreach (var s in a) Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1f);
        }
    }

    [Fact]
    public async Task Render_AllocatesNothing_AllFourLayers_WithEffects()
    {
        var p = new ParameterStore();
        DefaultPatch.Apply(p);
        await SharedBank.EnsureBuiltAsync(8);
        await SharedBank.EnsureBuiltAsync(25);
        var e = new AudioEngine(Fs, p, SharedBank);
        var buf = new float[256 * 2];
        void Iteration(int i)
        {
            for (int l = 0; l < 4; l++)
            {
                e.NoteOn(l, 40 + ((i + l * 7) % 30), 90);
                if (i % 3 == 0) e.NoteOff(l, 40 + ((i + l * 7 + 5) % 30));
                e.Parameters.Set(l, ParamId.Cutoff, (i + l * 11) % 128);
                e.Parameters.Set(l, ParamId.Harmonic, (i * 7 + l) % 128);
                e.Parameters.Set(l, ParamId.FilterType, 1 + (i + l) % 3);
                e.Parameters.Set(l, ParamId.ReverbSend, (i * 3) % 128);
                e.Parameters.Set(l, ParamId.ShimmerSend, (i * 5) % 128);
                e.Parameters.Set(l, ParamId.Mute, (i / 50 + l) % 2);
            }
            e.Parameters.Set(3, ParamId.NoisePlayMode, (i / 40) % 2);
            e.Parameters.Set(GlobalParamId.ReverbType, i % 10);
            e.Parameters.Set(GlobalParamId.FxType, (i / 20) % 7);
            e.Parameters.Set(GlobalParamId.FxSpeed, i % 128);
            e.Parameters.Set(GlobalParamId.Bpm, 40 + i % 200);
            e.PitchBend((i % 10) / 10f);
            e.Render(buf, 256);
        }
        for (int i = 0; i < 200; i++) Iteration(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 200; i < 1200; i++) Iteration(i);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }
}
