using Sambient.Dsp.Effects;

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
        // L1: SIN.S low drone-ish pad, octave -1, slow attack, long release, LPF
        Layer(p, 0, wave: 3, harmonic: 40, filter: FilterType.LowPass, cutoff: 70, reso: 25, attack: 80, release: 95,
              level: 62, pan: -12, octave: -1, reverbSend: 75, shimmerSend: 0);
        // L2: EV.OD pad
        Layer(p, 1, wave: 8, harmonic: 30, filter: FilterType.LowPass, cutoff: 88, reso: 25, attack: 70, release: 90,
              level: 58, pan: 14, octave: 0, reverbSend: 80, shimmerSend: 35);
        // L3: FM.PF bell / atmosphere: short attack, long release, band-pass
        Layer(p, 2, wave: 25, harmonic: 45, filter: FilterType.BandPass, cutoff: 92, reso: 40, attack: 12, release: 100,
              level: 50, pan: 0, octave: 0, reverbSend: 90, shimmerSend: 40);
        // L4: NOISE slot 1 (RAIN), low level, LOOP, mostly sample
        Layer(p, 3, wave: 1, harmonic: 112, filter: FilterType.Off, cutoff: 100, reso: 20, attack: 60, release: 80,
              level: 40, pan: 0, octave: 0, reverbSend: 50, shimmerSend: 0);
        p.Set(3, ParamId.NoisePlayMode, (float)Sambient.Dsp.Noise.SamplePlayMode.Loop);

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
