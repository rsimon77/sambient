using Sambient.Dsp.Effects;
using Sambient.Dsp.Params;

namespace Sambient.Engine;

/// <summary>
/// The startup sound: an ambient generator that sounds good as soon as the first key is pressed. Defined in one place;
/// later this becomes the first entry of the pattern-palette system. Values are hardware units (0..127).
/// The engine itself does not apply it: <see cref="ParameterStore"/> starts with neutral defaults so that tests and tools
/// get a predictable instrument; the application calls <see cref="Apply"/> once at startup.
/// </summary>
public static class DefaultPatch
{
    /// <summary>Layer display names (L1 .. L4).</summary>
    public static readonly string[] LayerNames = ["DRONE", "PAD", "ATMOS", "NOISE"];

    public static void Apply(ParameterStore p)
    {
        // L1: DRONE1 (2 Yang SIN.S, osc2 a fifth-ish drifting against osc1 by its detune): MOD on detune = very slow
        // fluctuation (+-12 cents at 0.06 Hz) on top of a few static cents, octave -1, slow attack, long release, LPF
        Layer(p, 0, wave: 3, harmonic: 40, filter: FilterType.LowPass, cutoff: 70, reso: 25, attack: 80, release: 95,
              level: 62, pan: -12, octave: -1, reverbSend: 75, shimmerSend: 0);
        SetStructure(p, 0, Structure.Drone1, pitch: 0, detune: 63.5f + 14f, balance: 63.5f);
        SetMod(p, 0, LfoShape.Sine, rate: 18, depth: 45);
        // L2: PAD1 (EV.OD Yang + 2 Yin spread by a few cents): slow HARMONIC MOD (Yang up / Yin down) and an LFO on the cutoff
        Layer(p, 1, wave: 8, harmonic: 30, filter: FilterType.LowPass, cutoff: 88, reso: 25, attack: 70, release: 90,
              level: 58, pan: 14, octave: 0, reverbSend: 80, shimmerSend: 35);
        SetStructure(p, 1, Structure.Pad1, pitch: 0, detune: 63.5f + 22f, balance: 63.5f);
        SetMod(p, 1, LfoShape.Sine, rate: 26, depth: 55);
        SetLfo(p, 1, 1, LfoShape.Triangle, LfoAssign.Cutoff, trig: 0, rate: 30, depth: 28);
        // L3: ATMOS2 FM bell / atmosphere: sine-ish carrier, modulator ratio ~3.5, a little feedback, band-pass
        Layer(p, 2, wave: 3, harmonic: 20, filter: FilterType.BandPass, cutoff: 92, reso: 40, attack: 12, release: 100,
              level: 50, pan: 0, octave: 0, reverbSend: 90, shimmerSend: 40);
        SetStructure(p, 2, Structure.Atmos2, pitch: ParamCurves.Atmos2RatioToPitch(3.5f), detune: 20, balance: 52);
        SetMod(p, 2, LfoShape.LowSine, rate: 60, depth: 30);
        // L4: NOISE slot 1 (RAIN), low level, LOOP, mostly sample; a slow LFO breathes the level
        Layer(p, 3, wave: 1, harmonic: 112, filter: FilterType.Off, cutoff: 100, reso: 20, attack: 60, release: 80,
              level: 40, pan: 0, octave: 0, reverbSend: 50, shimmerSend: 0);
        p.Set(3, ParamId.NoisePlayMode, (float)Sambient.Dsp.Noise.SamplePlayMode.Loop);
        SetLfo(p, 3, 1, LfoShape.SmoothRandom, LfoAssign.Level, trig: 0, rate: 16, depth: 40);

        for (int l = 0; l < ParameterStore.MaxLayers; l++) p.Set(l, ParamId.Mute, 0);

        p.Set(GlobalParamId.ReverbType, (float)ReverbType.LargeM);
        p.Set(GlobalParamId.ReverbMix, 75);
        p.Set(GlobalParamId.FxType, (float)MasterFxType.TapeDelay);
        p.Set(GlobalParamId.FxSpeed, 62);
        p.Set(GlobalParamId.FxAmount, 35);
        p.Set(GlobalParamId.Bpm, 90);
        p.Set(GlobalParamId.MasterVolume, 63.5f);
        p.Set(GlobalParamId.SelectedLayer, 1);
    }

    private static void SetStructure(ParameterStore p, int l, Structure structure, float pitch, float detune, float balance)
    {
        p.Set(l, ParamId.Structure, (float)structure);
        p.Set(l, ParamId.Pitch, pitch);
        p.Set(l, ParamId.Detune, detune);
        p.Set(l, ParamId.Balance, balance);
    }

    private static void SetMod(ParameterStore p, int l, LfoShape shape, float rate, float depth)
    {
        p.Set(l, ParamId.ModShape, (float)shape);
        p.Set(l, ParamId.ModRate, rate);
        p.Set(l, ParamId.ModDepth, depth);
    }

    private static void SetLfo(ParameterStore p, int l, int which, LfoShape shape, LfoAssign assign, int trig, float rate, float depth)
    {
        var (sh, a, t, r, d) = which == 1
            ? (ParamId.Lfo1Shape, ParamId.Lfo1Assign, ParamId.Lfo1Trig, ParamId.Lfo1Rate, ParamId.Lfo1Depth)
            : (ParamId.Lfo2Shape, ParamId.Lfo2Assign, ParamId.Lfo2Trig, ParamId.Lfo2Rate, ParamId.Lfo2Depth);
        p.Set(l, sh, (float)shape); p.Set(l, a, (float)assign); p.Set(l, t, trig); p.Set(l, r, rate); p.Set(l, d, depth);
    }

    private static void Layer(ParameterStore p, int l, int wave, float harmonic, FilterType filter, float cutoff, float reso,
        float attack, float release, float level, float pan, int octave, float reverbSend, float shimmerSend)
    {
        p.Set(l, ParamId.Wave, wave);
        p.Set(l, ParamId.Harmonic, harmonic);
        p.Set(l, ParamId.FilterType, (float)filter);
        p.Set(l, ParamId.Cutoff, cutoff);
        p.Set(l, ParamId.Resonance, reso);
        p.Set(l, ParamId.Attack, attack);
        p.Set(l, ParamId.Release, release);
        p.Set(l, ParamId.Level, level);
        p.Set(l, ParamId.Pan, pan);
        p.Set(l, ParamId.Octave, octave);
        p.Set(l, ParamId.ReverbSend, reverbSend);
        p.Set(l, ParamId.ShimmerSend, shimmerSend);
    }
}
