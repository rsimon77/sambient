using Sambient.Dsp.Effects;
using Sambient.Dsp.Noise;
using Sambient.Dsp.Wavetables;

namespace Sambient.Engine;

/// <summary>
/// JIT warm-up. The first time the audio callback runs a code path (a structure, a filter type, an LFO shape, a reverb type, a
/// wave swap, ...) the JIT compiles it on the audio thread, which can take longer than a whole buffer (a 9 ms first WAVE swap
/// was measured against a 10.7 ms budget at 512 frames). <see cref="WarmUp"/> renders ~2 s of audio through a throw-away engine
/// that exercises every path, so the methods are already compiled when the live engine needs them.
/// </summary>
public static class EngineWarmUp
{
    private const int Block = 512;

    /// <summary>
    /// Runs the warm-up on the CALLER's thread (call it from a background thread at startup; it takes well under a second of CPU).
    /// It builds its own <see cref="ParameterStore"/> and <see cref="AudioEngine"/> and never touches a live engine or its
    /// parameters. <paramref name="bank"/> / <paramref name="noise"/> are only READ (shared so no extra waves are built): the
    /// SIN.S wavetable and noise slot 1 are requested (waiting for them if they are not built yet; the live engine needs them
    /// anyway) and the other waves / slots that are already built are used for the wave swaps.
    /// </summary>
    public static void WarmUp(WavetableBank bank, NoiseBank noise, int sampleRate = 48000)
    {
        bank.EnsureBuiltAsync(WavetableBank.FallbackIndex).Wait();
        noise.EnsureBuiltAsync(1).Wait();

        // waves / noise slots to swap between: SIN.S plus whatever else is built right now (never triggers a build)
        var waves = new List<int> { WavetableBank.FallbackIndex };
        for (int w = 1; w <= WavetableBank.MaxWaves && waves.Count < 4; w++)
            if (w != WavetableBank.FallbackIndex && WaveRecipesIsReady(bank, w)) waves.Add(w);
        var slots = new List<int> { 1 };
        for (int s = 2; s <= NoiseBank.SlotCount && slots.Count < 3; s++) if (noise.IsReady(s)) slots.Add(s);

        var p = new ParameterStore();
        DefaultPatch.Apply(p);
        for (int l = 0; l < AudioEngine.SynthLayerCount; l++) p.Set(l, ParamId.Wave, waves[l % waves.Count]);
        p.Set(AudioEngine.NoiseLayerIndex, ParamId.Wave, slots[0]);
        var e = new AudioEngine(sampleRate, p, bank, noise);
        var buf = new float[Block * 2];
        void Render(int blocks = 1) { for (int i = 0; i < blocks; i++) e.Render(buf, Block); }

        // ---- notes on every layer (chord on the synth layers, one key on the noise layer)
        for (int l = 0; l < AudioEngine.SynthLayerCount; l++) foreach (int n in new[] { 48, 55, 60, 64, 67 }) e.NoteOn(l, n, 100);
        e.NoteOn(AudioEngine.NoiseLayerIndex, 60, 100);
        Render(3);

        // ---- structures (with MOD and both LFOs running so every modulation branch is used)
        for (int l = 0; l < AudioEngine.SynthLayerCount; l++)
        {
            e.SetParam(l, ParamId.ModDepth, 90);
            e.SetParam(l, ParamId.Lfo1Depth, 80); e.SetParam(l, ParamId.Lfo2Depth, 80);
            e.SetParam(l, ParamId.Lfo1Assign, (float)LfoAssign.Cutoff); e.SetParam(l, ParamId.Lfo2Assign, (float)LfoAssign.Harmonic);
        }
        foreach (Structure st in Enum.GetValues<Structure>())
        {
            for (int l = 0; l < AudioEngine.SynthLayerCount; l++) e.SetParam(l, ParamId.Structure, (float)st);
            Render(2);
        }

        // ---- filter types on all layers
        foreach (FilterType ft in new[] { FilterType.LowPass, FilterType.HighPass, FilterType.BandPass, FilterType.Off, FilterType.LowPass })
        {
            for (int l = 0; l < AudioEngine.LayerCount; l++) e.SetParam(l, ParamId.FilterType, (float)ft);
            Render(2);
        }

        // ---- LFO / MOD shapes, assignments, triggers (retriggered notes exercise the per-voice LFOs)
        int shapes = Enum.GetValues<LfoShape>().Length, assigns = Enum.GetValues<LfoAssign>().Length;
        for (int k = 0; k < Math.Max(shapes, assigns); k++)
        {
            for (int l = 0; l < AudioEngine.LayerCount; l++)
            {
                e.SetParam(l, ParamId.Lfo1Shape, k % shapes); e.SetParam(l, ParamId.Lfo2Shape, (k + 7) % shapes);
                e.SetParam(l, ParamId.ModShape, (k + 3) % shapes);
                e.SetParam(l, ParamId.Lfo1Assign, k % assigns); e.SetParam(l, ParamId.Lfo2Assign, (k + 5) % assigns);
                e.SetParam(l, ParamId.Lfo1Trig, new[] { 0, 9, 3 }[k % 3]); e.SetParam(l, ParamId.Lfo2Trig, new[] { 9, 0, 1 }[k % 3]);
                e.SetParam(l, ParamId.Lfo1Depth, 90); e.SetParam(l, ParamId.Lfo2Depth, 90);
                e.SetParam(l, ParamId.ModDepth, 70);
            }
            if ((k & 3) == 0) { e.NoteOn(1, 60, 80); e.NoteOn(3, 60, 80); }
            Render();
        }
        for (int l = 0; l < AudioEngine.LayerCount; l++)
        {
            e.SetParam(l, ParamId.Lfo1Assign, 0); e.SetParam(l, ParamId.Lfo2Assign, 0);
            e.SetParam(l, ParamId.Lfo1Assign, (float)LfoAssign.Level); e.SetParam(l, ParamId.Lfo2Assign, (float)LfoAssign.Pan);
        }
        Render(2);

        // ---- wave swaps (synth wavetables and noise slots), noise play mode, continuous jumps
        for (int i = 0; i < 4; i++)
        {
            for (int l = 0; l < AudioEngine.SynthLayerCount; l++) e.SetParam(l, ParamId.Wave, waves[(i + l + 1) % waves.Count]);
            e.SetParam(AudioEngine.NoiseLayerIndex, ParamId.Wave, slots[(i + 1) % slots.Count]);
            e.SetParam(AudioEngine.NoiseLayerIndex, ParamId.NoisePlayMode, i & 1);
            Render(2);
        }
        e.SetParam(AudioEngine.NoiseLayerIndex, ParamId.NoisePlayMode, 0);
        for (int l = 0; l < AudioEngine.LayerCount; l++)
        {
            e.SetParam(l, ParamId.Cutoff, 20); e.SetParam(l, ParamId.Resonance, 110); e.SetParam(l, ParamId.Level, 30);
            e.SetParam(l, ParamId.Pan, -50); e.SetParam(l, ParamId.Harmonic, 100); e.SetParam(l, ParamId.Pitch, 100);
            e.SetParam(l, ParamId.Detune, 100); e.SetParam(l, ParamId.Balance, 20); e.SetParam(l, ParamId.ReverbSend, 100);
            e.SetParam(l, ParamId.ShimmerSend, 100); e.SetParam(l, ParamId.Attack, 5); e.SetParam(l, ParamId.Release, 40);
        }
        Render(3);

        // ---- voice modes (all transitions incl. UNISON and ARP with every arpeggio type), glide, octave, mute
        foreach (VoiceMode m in new[] { VoiceMode.Mono, VoiceMode.Legato, VoiceMode.Unison, VoiceMode.Arp, VoiceMode.Poly })
        {
            for (int l = 0; l < AudioEngine.LayerCount; l++) { e.SetParam(l, ParamId.VoiceMode, (float)m); e.SetParam(l, ParamId.Glide, 60); }
            Render(2);
            e.NoteOn(0, 72, 90); e.NoteOn(3, 62, 90);
            Render();
        }
        for (int l = 0; l < AudioEngine.LayerCount; l++) { e.SetParam(l, ParamId.VoiceMode, (float)VoiceMode.Arp); e.SetParam(l, ParamId.StepNote, (float)StepNote.ThirtySecond1_32); }
        e.SetGlobal(GlobalParamId.Bpm, 250);
        foreach (ArpType t in Enum.GetValues<ArpType>())
        {
            for (int l = 0; l < AudioEngine.LayerCount; l++) e.SetParam(l, ParamId.ArpType, (float)t);
            Render();
        }
        for (int l = 0; l < AudioEngine.LayerCount; l++) e.SetParam(l, ParamId.VoiceMode, (float)VoiceMode.Poly);
        foreach (int oct in new[] { 1, -1, 0 })
        {
            for (int l = 0; l < AudioEngine.LayerCount; l++) e.SetParam(l, ParamId.Octave, oct);
            Render();
        }
        e.SetParam(0, ParamId.Mute, 1); Render(); e.SetParam(0, ParamId.Mute, 0); Render();

        // ---- note events: voice stealing, re-trigger, sustain pedal, HOLD, pitch bend, mod wheel, panic
        for (int n = 30; n < 56; n++) { e.NoteOn(1, n, 100); e.NoteOn(0, n, 60); e.NoteOn(3, n, 100); }
        Render();
        e.NoteOn(1, 40, 20); e.PitchBend(0.5f); e.ModWheel(0.7f); e.Sustain(true);
        Render();
        for (int n = 30; n < 40; n++) { e.NoteOff(1, n); e.NoteOff(3, n); }
        e.HoldToggle(1, 70, 100); e.HoldToggle(1, 70, 100); e.ClearHolds(1); e.ClearAllHolds();
        Render();
        e.Sustain(false); e.PitchBend(0f); e.ModWheel(0f);
        Render();

        // ---- tuning
        for (int t = 0; t <= (int)Temperament.Planetary; t++)
        {
            e.SetGlobal(GlobalParamId.Temperament, t);
            for (int l = 0; l < AudioEngine.LayerCount; l++) e.SetParam(l, ParamId.TuningKey, t % 5);
            Render();
        }
        e.SetGlobal(GlobalParamId.MasterTuneCents, 30); e.SetGlobal(GlobalParamId.TuneAllCents, 200); Render();
        e.SetGlobal(GlobalParamId.Temperament, 0); e.SetGlobal(GlobalParamId.MasterTuneCents, 0); e.SetGlobal(GlobalParamId.TuneAllCents, 0);

        // ---- reverb types (each size class, tone, Off) and master FX types, routing, levels
        e.SetGlobal(GlobalParamId.ReverbMix, 90); e.SetGlobal(GlobalParamId.FxAmount, 80); e.SetGlobal(GlobalParamId.FxSpeed, 60);
        foreach (ReverbType rt in Enum.GetValues<ReverbType>())
        {
            e.SetGlobal(GlobalParamId.ReverbType, (float)rt);
            Render(2);
        }
        e.SetGlobal(GlobalParamId.ReverbType, (float)ReverbType.InfinityL); Render(2);
        e.SetGlobal(GlobalParamId.ReverbType, (float)ReverbType.SmallM); Render(2);
        foreach (MasterFxType ft in Enum.GetValues<MasterFxType>())
        {
            e.SetGlobal(GlobalParamId.FxType, (float)ft);
            Render(3);
            e.SetGlobal(GlobalParamId.FxAmount, 120); e.SetGlobal(GlobalParamId.FxSpeed, 100); Render();
        }
        e.SetGlobal(GlobalParamId.FxType, (float)MasterFxType.Overdrive);
        e.SetGlobal(GlobalParamId.FxRouting, 1); e.SetGlobal(GlobalParamId.PatternLevel, 40); Render(3);
        e.SetGlobal(GlobalParamId.FxRouting, 0); Render(3);
        e.SetGlobal(GlobalParamId.MasterVolume, 100); e.SetGlobal(GlobalParamId.PatternLevel, 63.5f); Render();

        e.AllNotesOff();
        Render(2);
    }

    private static bool WaveRecipesIsReady(WavetableBank bank, int index) => WaveRecipes.IsImplemented(index) && bank.IsReady(index);
}
