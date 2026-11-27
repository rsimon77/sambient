using Sambient.Dsp.Effects;
using Sambient.Engine;

namespace Sambient.Seq;

/// <summary>
/// Sambient's own factory palettes (code-defined, never written to disk unless the user saves over a slot). Bank 1 holds twelve
/// hand-designed palettes (drones, pads, generative arpeggios, noise textures, temperaments) plus four variations; banks 2..4 are
/// variations of those (different tuning system, key, tempo, reverb) so every preset slot makes a sound.
/// </summary>
public static class FactoryPresets
{
    private const int Wh = (int)StepNote.Whole1_1, Hf = (int)StepNote.Half1_2, Qt = (int)StepNote.Quarter1_4,
        E8 = (int)StepNote.Eighth1_8, S16 = (int)StepNote.Sixteenth1_16, Br = (int)StepNote.Breve2_1, Mx = (int)StepNote.Maxima8_1,
        Lg = (int)StepNote.Longa4_1;

    private static readonly Lazy<PatternPalette[]> Designed = new(BuildDesigned);

    public static string[] DesignedNames => Designed.Value.Select(p => p.Name).ToArray();

    /// <summary>The factory palette of a preset slot (banks 0..3), as a fresh copy; null for user banks.</summary>
    public static PatternPalette? Get(int bank, int slot)
    {
        if (bank is < 0 or >= PatternBank.PresetBanks || slot is < 0 or >= PatternBank.SlotsPerBank) return null;
        var d = Designed.Value;
        int idx = bank * PatternBank.SlotsPerBank + slot;
        if (bank == 0 && slot < d.Length) return d[slot].Clone();
        return Variation(d[idx % d.Length], idx);
    }

    // Layer LEVEL curve (see ParamCurves.LevelGain): dB-linear from -60 dB at 0 to 0 dB at 63.5, then up to +6 dB at 127.
    private static float KnobToDb(float k) => k <= 63.5f ? -60f * (1f - k / 63.5f) : 6f * (k - 63.5f) / 63.5f;
    private static float DbToKnob(float db) => Math.Clamp(db <= 0f ? 63.5f * (1f + db / 60f) : 63.5f + db * 63.5f / 6f, 0f, 127f);

    private static readonly Temperament[] VarTemper =
    {
        Temperament.PureMajor, Temperament.PythagoreanSharp, Temperament.PureMinor, Temperament.Solfeggio, Temperament.PythagoreanFlat,
        Temperament.Chakra, Temperament.Planetary, Temperament.Equal,
    };

    private static PatternPalette Variation(PatternPalette src, int idx)
    {
        var p = src.Clone();
        int v = idx % 7 + 1;
        float trim = VarTrimDb[idx - 12];
        string baseName = src.Name.TrimEnd();
        p.Name = (baseName.Length > 6 ? baseName[..6] : baseName) + (idx / 10 % 10) + (idx % 10);
        var tmp = VarTemper[idx % VarTemper.Length];
        p.SetGlobal(GlobalParamId.Temperament, (float)tmp);
        int keyCount = Tuning.KeyCount(tmp);
        for (int l = 0; l < 4; l++) p.SetLayer(l, ParamId.TuningKey, (idx * 3 + l) % keyCount);
        p.SetGlobal(GlobalParamId.Transpose, new[] { 0, 2, -5, 7, -2, 5, -7 }[idx % 7]);
        p.SetGlobal(GlobalParamId.Bpm, Math.Clamp(p.GetGlobal(GlobalParamId.Bpm) * (0.8f + 0.07f * v), 40, 250));
        p.SetGlobal(GlobalParamId.ReverbType, (float)(ReverbType.SmallL + (idx * 2) % 9));
        if (p.GetGlobal(GlobalParamId.ReverbType) < 1) p.SetGlobal(GlobalParamId.ReverbType, (float)ReverbType.LargeM);
        for (int l = 0; l < 3; l++) p.SetLayer(l, ParamId.Harmonic, (p.GetLayer(l, ParamId.Harmonic) + 17 * v) % 128);
        for (int l = 0; l < 3; l++) p.SetLayer(l, ParamId.Cutoff, Math.Clamp(p.GetLayer(l, ParamId.Cutoff) + (v - 4) * 4, 30, 127));
        for (int l = 0; l < 4; l++) p.SetLayer(l, ParamId.Level, DbToKnob(KnobToDb(p.GetLayer(l, ParamId.Level)) + trim));
        return p;
    }

    /// <summary>
    /// Loudness trim (dB, applied to all four layer levels) of the generated variations, indexed by slot index minus 12 (bank 1 slots 13..16,
    /// then banks 2..4). A variation changes tuning, tempo, reverb type and cutoffs, which moves its loudness away from its source palette,
    /// so each one is measured with <c>WaveDump --preset-report</c> and trimmed to about -24 dBFS RMS.
    /// </summary>
    private static readonly float[] VarTrimDb =
    {
        1.2f, 0.2f, -0.3f, -1.1f, 0.7f, -3.5f, 0.8f, -2.6f, -0.5f, 1.9f, -2.2f, -2.5f, -2.2f,
        -4.1f, -2.8f, 0.3f, 5.4f, -3.3f, -3.4f, -3.4f, -2.9f, 3.1f, -0.3f, 6.7f, -1.8f, -3f,
        -4.8f, -2.7f, 7.2f, -3.5f, -2.4f, -3.5f, -4f, -0.8f, -4.4f, 1.9f, 0.2f, 1.9f, 0.5f,
        -2.4f, 2.2f, -2.8f, -0.4f, -0.1f, -2.4f, -1f, -0.3f, -0.3f, -2.2f, -0.7f, -2.8f, -0.5f,
    };

    // ------------------------------------------------------------------ builder

    private sealed class B
    {
        public readonly PatternPalette P;
        private readonly float _trimDb;

        /// <param name="trimDb">Loudness trim of the whole palette in dB, applied to all four layer levels (the mix between layers is kept).
        /// Chosen so the rendered palette sits at about -24 dBFS RMS (see <c>WaveDump --preset-report</c>).</param>
        public B(string name, float bpm, ReverbType rvb, float rvbMix, MasterFxType fx, float fxSpeed, float fxAmt, float trimDb = 0)
        {
            _trimDb = trimDb;
            P = new PatternPalette(name);
            P.SetGlobal(GlobalParamId.Bpm, bpm);
            P.SetGlobal(GlobalParamId.ReverbType, (float)rvb); P.SetGlobal(GlobalParamId.ReverbMix, rvbMix);
            P.SetGlobal(GlobalParamId.FxType, (float)fx); P.SetGlobal(GlobalParamId.FxSpeed, fxSpeed); P.SetGlobal(GlobalParamId.FxAmount, fxAmt);
            for (int l = 0; l < 4; l++) { P.SetLayer(l, ParamId.Mute, 0); Track(l, 1, Wh); }
            P.SetLayer(3, ParamId.NoisePlayMode, (float)Sambient.Dsp.Noise.SamplePlayMode.Loop);
        }

        /// <summary>Preset levels are written relative to a quiet mix (+14 knob units, capped at 72), then trimmed by the palette's loudness trim.</summary>
        private float Lv(float level) => DbToKnob(KnobToDb(Math.Min(level + 14f, 72f)) + _trimDb);


        public B Temper(Temperament t, params int[] keys)
        {
            P.SetGlobal(GlobalParamId.Temperament, (float)t);
            for (int l = 0; l < keys.Length && l < 4; l++) P.SetLayer(l, ParamId.TuningKey, keys[l]);
            return this;
        }

        public B Syn(int l, Structure st, int wave, float harm, FilterType ft, float cutoff, float reso, float atk, float rel, float level,
            float pan, int oct, float rev, float shim, float pitch = 0, float detune = 63.5f, float bal = 63.5f)
        {
            P.SetLayer(l, ParamId.Structure, (float)st); P.SetLayer(l, ParamId.Wave, wave); P.SetLayer(l, ParamId.Harmonic, harm);
            P.SetLayer(l, ParamId.FilterType, (float)ft); P.SetLayer(l, ParamId.Cutoff, cutoff); P.SetLayer(l, ParamId.Resonance, reso);
            P.SetLayer(l, ParamId.Attack, atk); P.SetLayer(l, ParamId.Release, rel); P.SetLayer(l, ParamId.Level, Lv(level));
            P.SetLayer(l, ParamId.Pan, pan); P.SetLayer(l, ParamId.Octave, oct);
            P.SetLayer(l, ParamId.ReverbSend, rev); P.SetLayer(l, ParamId.ShimmerSend, shim);
            P.SetLayer(l, ParamId.Pitch, pitch); P.SetLayer(l, ParamId.Detune, detune); P.SetLayer(l, ParamId.Balance, bal);
            return this;
        }

        public B Noise(int slot, float balance, FilterType ft, float cutoff, float atk, float rel, float level, float pan, float rev, float shim)
        {
            const int l = 3;
            P.SetLayer(l, ParamId.Wave, slot); P.SetLayer(l, ParamId.Harmonic, balance); P.SetLayer(l, ParamId.FilterType, (float)ft);
            P.SetLayer(l, ParamId.Cutoff, cutoff); P.SetLayer(l, ParamId.Attack, atk); P.SetLayer(l, ParamId.Release, rel);
            P.SetLayer(l, ParamId.Level, Lv(level)); P.SetLayer(l, ParamId.Pan, pan);
            P.SetLayer(l, ParamId.ReverbSend, rev); P.SetLayer(l, ParamId.ShimmerSend, shim);
            return this;
        }

        public B Mod(int l, LfoShape shape, float rate, float depth)
        {
            P.SetLayer(l, ParamId.ModShape, (float)shape); P.SetLayer(l, ParamId.ModRate, rate); P.SetLayer(l, ParamId.ModDepth, depth);
            return this;
        }

        public B Lfo(int l, int which, LfoShape shape, LfoAssign assign, int trig, float rate, float depth)
        {
            var (sh, a, t, r, d) = which == 1
                ? (ParamId.Lfo1Shape, ParamId.Lfo1Assign, ParamId.Lfo1Trig, ParamId.Lfo1Rate, ParamId.Lfo1Depth)
                : (ParamId.Lfo2Shape, ParamId.Lfo2Assign, ParamId.Lfo2Trig, ParamId.Lfo2Rate, ParamId.Lfo2Depth);
            P.SetLayer(l, sh, (float)shape); P.SetLayer(l, a, (float)assign); P.SetLayer(l, t, trig); P.SetLayer(l, r, rate); P.SetLayer(l, d, depth);
            return this;
        }

        public B Arp(int l, ArpType type)
        {
            P.SetLayer(l, ParamId.VoiceMode, (float)VoiceMode.Arp); P.SetLayer(l, ParamId.ArpType, (float)type);
            return this;
        }

        public B Track(int l, int length, int stepNote)
        {
            P.SetLayer(l, ParamId.SeqLength, length); P.SetLayer(l, ParamId.StepNote, stepNote);
            return this;
        }

        public B Chord(int l, int step, int tie, int vel, params int[] notes)
        {
            foreach (var n in notes) P.Tracks[l].Steps[step].Notes.Add(StepNoteData.Make(n, vel, tie));
            return this;
        }

        public B Prob(int l, int step, int percent) { P.Tracks[l].Steps[step].Probability = percent; return this; }

        public B Lock(int l, int step, ParamId id, float v) { P.Tracks[l].Steps[step].Locks[id] = v; return this; }
    }

    // ------------------------------------------------------------------ the twelve designed palettes

    // Loudness: every palette carries a `trimDb` (all four layer levels, mix kept) so it renders at about -24 dBFS RMS with the peak well below the
    // -3 dBFS soft clipper; the comment on each constructor line is the measured result (`WaveDump --preset-report --bank 1`, 30 s, first 4 s skipped).
    // SLOWTIDE sits at -24.7 because its drone layer is already at the +6 dB LEVEL ceiling.
    private static PatternPalette[] BuildDesigned() =>
    [
        DeepDrift(), SlowTide(), GlassArp(), Morning(), Forest(), Chakra(), Planetary(), Nightfall(), Crystal(), RainDay(), Breathe(), Polymeter(),
    ];

    // 1. DEEPDRFT: the classic ambient bed. Two drifting drones, a slow pad, rare bells, wind.
    private static PatternPalette DeepDrift()
    {
        var b = new B("DEEPDRFT", 60, ReverbType.InfinityM, 88, MasterFxType.TapeDelay, 55, 30, trimDb: -3.6f);   // RMS -20.4 -> -24.0 dBFS, peak -12.5 dBFS
        b.Syn(0, Structure.Drone1, 3, 40, FilterType.LowPass, 70, 25, 90, 100, 60, -10, -1, 80, 0, detune: 77.5f).Mod(0, LfoShape.Sine, 18, 45)
         .Track(0, 8, Wh).Chord(0, 0, 4, 90, 36, 43).Chord(0, 4, 4, 90, 32, 39);
        b.Syn(1, Structure.Pad1, 8, 30, FilterType.LowPass, 88, 25, 70, 90, 54, 14, 0, 80, 35, detune: 85).Mod(1, LfoShape.Sine, 26, 55)
         .Lfo(1, 1, LfoShape.Triangle, LfoAssign.Cutoff, 0, 30, 28)
         .Track(1, 8, Wh).Chord(1, 0, 4, 80, 60, 63, 67, 70).Chord(1, 4, 4, 80, 56, 60, 63).Lock(1, 4, ParamId.Harmonic, 62);
        b.Syn(2, Structure.Atmos2, 3, 20, FilterType.BandPass, 92, 40, 12, 100, 44, 0, 0, 90, 40, pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(3.5f), detune: 20, bal: 52)
         .Mod(2, LfoShape.LowSine, 60, 30).Track(2, 16, Hf)
         .Chord(2, 2, 1, 70, 84).Chord(2, 5, 1, 66, 79).Chord(2, 9, 1, 72, 75).Chord(2, 12, 1, 64, 82)
         .Prob(2, 2, 60).Prob(2, 5, 60).Prob(2, 9, 60).Prob(2, 12, 60);
        b.Noise(6, 112, FilterType.LowPass, 80, 70, 80, 34, 0, 60, 0).Lfo(3, 1, LfoShape.SmoothRandom, LfoAssign.Level, 0, 16, 40)
         .Track(3, 1, Mx).Chord(3, 0, 1, 90, 60);
        return b.P;
    }

    // 2. SLOWTIDE: sea, swelling pads in a minor-ninth cycle.
    private static PatternPalette SlowTide()
    {
        var b = new B("SLOWTIDE", 50, ReverbType.LargeH, 80, MasterFxType.Chorus, 35, 40, trimDb: 6.4f);   // RMS -30.4 -> -24.7 dBFS, peak -11.3 dBFS
        b.Syn(0, Structure.Drone2, 3, 30, FilterType.LowPass, 64, 20, 100, 105, 56, 0, -1, 70, 0, pitch: 0, detune: 70).Mod(0, LfoShape.LowTriangle, 25, 40)
         .Track(0, 4, Lg).Chord(0, 0, 1, 85, 33).Chord(0, 1, 1, 85, 29).Chord(0, 2, 1, 85, 31).Chord(0, 3, 1, 85, 28);
        b.Syn(1, Structure.Pad2, 8, 35, FilterType.LowPass, 82, 28, 105, 100, 55, -16, 0, 85, 50, detune: 80).Mod(1, LfoShape.Sine, 22, 50)
         .Track(1, 4, Lg).Chord(1, 0, 1, 75, 57, 60, 64, 67, 71).Chord(1, 1, 1, 75, 53, 57, 60, 64, 67).Chord(1, 2, 1, 75, 55, 59, 62, 65, 69).Chord(1, 3, 1, 75, 52, 55, 59, 62, 66);
        b.Syn(2, Structure.Pad1, 22, 50, FilterType.BandPass, 78, 35, 100, 100, 40, 18, 1, 90, 70).Lfo(2, 1, LfoShape.SmoothRandom, LfoAssign.Harmonic, 0, 20, 45)
         .Track(2, 4, Lg).Chord(2, 0, 1, 60, 76, 79).Chord(2, 1, 1, 60, 72, 76).Chord(2, 2, 1, 60, 74, 77).Chord(2, 3, 1, 60, 71, 74);
        b.Noise(5, 110, FilterType.LowPass, 70, 90, 90, 44, 0, 50, 0).Lfo(3, 1, LfoShape.Sine, LfoAssign.Level, 0, 10, 55)
         .Track(3, 1, Mx).Chord(3, 0, 1, 90, 60);
        return b.P;
    }

    // 3. GLASSARP: generative arpeggios on a held chord, probability and harmonic p-locks.
    private static PatternPalette GlassArp()
    {
        var b = new B("GLASSARP", 72, ReverbType.LargeM, 76, MasterFxType.TapeDelay, 70, 45, trimDb: 4.0f);   // RMS -28.0 -> -24.0 dBFS, peak -12.1 dBFS
        b.Syn(0, Structure.Drone1, 3, 36, FilterType.LowPass, 66, 25, 85, 95, 52, -8, -1, 60, 0, detune: 73).Mod(0, LfoShape.Sine, 14, 35)
         .Track(0, 8, Wh).Chord(0, 0, 4, 85, 38).Chord(0, 4, 4, 85, 33);
        b.Syn(1, Structure.Pad1, 8, 28, FilterType.LowPass, 90, 25, 40, 80, 42, 16, 0, 75, 20, detune: 75).Arp(1, ArpType.UpDown)
         .Track(1, 16, E8).Chord(1, 0, 8, 80, 62, 65, 69, 72).Chord(1, 8, 8, 80, 58, 62, 65, 69)
         .Prob(1, 0, 100);
        b.Syn(2, Structure.Atmos2, 25, 24, FilterType.BandPass, 96, 35, 8, 95, 40, -14, 0, 90, 45,
            pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(2.0f), detune: 30, bal: 60).Arp(2, ArpType.Random)
         .Track(2, 16, S16).Chord(2, 0, 16, 72, 74, 77, 81, 84).Prob(2, 0, 75).Lock(2, 0, ParamId.Harmonic, 40);
        for (int s = 4; s < 16; s += 4) b.Lock(2, s, ParamId.Harmonic, 40 + s * 6).Lock(2, s, ParamId.Cutoff, 90 - s);
        b.Noise(1, 100, FilterType.HighPass, 40, 80, 70, 22, 8, 60, 20).Track(3, 1, Mx).Chord(3, 0, 1, 80, 60);
        return b.P;
    }

    // 4. MORNING: just-intonation major (PR.MJ in C): open fifths and major sevenths, bright and calm.
    private static PatternPalette Morning()
    {
        var b = new B("MORNING", 66, ReverbType.LargeL, 78, MasterFxType.Off, 64, 0, trimDb: -1.4f);   // RMS -22.6 -> -24.0 dBFS, peak -12.3 dBFS
        b.Temper(Temperament.PureMajor, 0, 0, 0, 0);
        b.Syn(0, Structure.Drone2, 3, 34, FilterType.LowPass, 72, 20, 90, 100, 56, 0, -1, 70, 0, detune: 72)
         .Track(0, 8, Wh).Chord(0, 0, 4, 85, 36, 43).Chord(0, 4, 4, 85, 41, 48);
        b.Syn(1, Structure.Pad2, 8, 40, FilterType.LowPass, 98, 22, 75, 90, 52, -10, 0, 80, 40, detune: 78).Mod(1, LfoShape.Sine, 30, 40)
         .Track(1, 8, Wh).Chord(1, 0, 4, 78, 64, 67, 71, 74).Chord(1, 4, 4, 78, 65, 69, 72, 76);
        b.Syn(2, Structure.Atmos1, 6, 30, FilterType.LowPass, 100, 20, 30, 95, 36, 14, 1, 85, 55).Track(2, 16, Hf)
         .Chord(2, 0, 2, 66, 83).Chord(2, 6, 2, 62, 86).Chord(2, 10, 2, 62, 79).Chord(2, 13, 2, 60, 88).Prob(2, 6, 70).Prob(2, 10, 60).Prob(2, 13, 50);
        b.Noise(8, 96, FilterType.LowPass, 90, 85, 85, 26, 0, 70, 30).Track(3, 1, Mx).Chord(3, 0, 1, 80, 60);
        return b.P;
    }

    // 5. FORESTRN: noise-led texture; the noise sample changes per step through WAVE locks (forest / wind / rain / woods).
    private static PatternPalette Forest()
    {
        var b = new B("FORESTRN", 58, ReverbType.SmallH, 70, MasterFxType.TiltEq, 50, 40, trimDb: 4.9f);   // RMS -28.9 -> -24.0 dBFS, peak -12.1 dBFS
        b.Temper(Temperament.Solfeggio, 4, 4, 4, 4);       // 528 Hz
        b.Syn(0, Structure.Drone1, 3, 30, FilterType.LowPass, 62, 25, 95, 100, 50, -6, -1, 70, 0, detune: 76).Track(0, 4, Lg).Chord(0, 0, 1, 80, 36).Chord(0, 2, 1, 80, 38);
        b.Syn(1, Structure.Pad1, 21, 36, FilterType.LowPass, 80, 30, 80, 95, 40, 12, 0, 85, 35, detune: 70).Mod(1, LfoShape.SmoothRandom, 20, 45)
         .Track(1, 4, Lg).Chord(1, 0, 1, 70, 60, 67, 72).Chord(1, 2, 1, 70, 62, 65, 69);
        b.Syn(2, Structure.Atmos2, 3, 20, FilterType.BandPass, 94, 40, 15, 100, 28, -12, 0, 90, 40,
            pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(4.0f), detune: 24, bal: 48)
         .Track(2, 32, Qt).Chord(2, 3, 1, 62, 84).Chord(2, 13, 1, 60, 79).Chord(2, 22, 1, 62, 88).Prob(2, 3, 50).Prob(2, 13, 50).Prob(2, 22, 50);
        b.Noise(4, 120, FilterType.Off, 100, 60, 70, 50, 0, 55, 0).Lfo(3, 1, LfoShape.SmoothRandom, LfoAssign.Pan, 0, 14, 50)
         .Track(3, 4, Lg).Chord(3, 0, 1, 90, 60).Chord(3, 1, 1, 90, 60).Chord(3, 2, 1, 90, 60).Chord(3, 3, 1, 90, 60)
         .Lock(3, 1, ParamId.Wave, 6).Lock(3, 2, ParamId.Wave, 7).Lock(3, 3, ParamId.Wave, 1);
        return b.P;
    }

    // 6. CHAKRA: Chakra temperament (C4 = 320 Hz), a long meditative drone cycle.
    private static PatternPalette Chakra()
    {
        var b = new B("CHAKRA", 54, ReverbType.InfinityL, 84, MasterFxType.Off, 64, 0, trimDb: 1.2f);   // RMS -25.2 -> -24.0 dBFS, peak -13.2 dBFS
        b.Temper(Temperament.Chakra, 2, 2, 2, 2);
        b.Syn(0, Structure.Drone2, 3, 30, FilterType.LowPass, 66, 20, 100, 110, 58, 0, -1, 75, 0, detune: 70).Mod(0, LfoShape.LowSine, 20, 35)
         .Track(0, 2, Lg).Chord(0, 0, 2, 85, 36, 43);
        b.Syn(1, Structure.Pad1, 22, 40, FilterType.LowPass, 84, 25, 100, 105, 48, 0, 0, 85, 50, detune: 72).Mod(1, LfoShape.Sine, 16, 50)
         .Track(1, 2, Lg).Chord(1, 0, 1, 70, 60, 64, 67).Chord(1, 1, 1, 70, 62, 67, 71);
        b.Syn(2, Structure.Atmos1, 6, 36, FilterType.LowPass, 96, 25, 90, 100, 30, 0, 1, 90, 60).Track(2, 4, Lg)
         .Chord(2, 0, 1, 60, 72).Chord(2, 2, 1, 60, 76);
        b.Noise(5, 118, FilterType.LowPass, 74, 90, 90, 28, 0, 60, 0).Track(3, 1, Mx).Chord(3, 0, 1, 80, 60);
        return b.P;
    }

    // 7. PLNTARY: Planetary temperament (4th planet pitch), airy bells over a drone.
    private static PatternPalette Planetary()
    {
        var b = new B("PLNTARY", 62, ReverbType.LargeM, 82, MasterFxType.ReverseDelay, 60, 35, trimDb: 0f);   // RMS -23.9 -> -23.9 dBFS, peak -10.2 dBFS
        b.Temper(Temperament.Planetary, 3, 3, 3, 3);
        b.Syn(0, Structure.Drone1, 3, 44, FilterType.LowPass, 68, 25, 95, 100, 54, 0, -1, 80, 0, detune: 80).Mod(0, LfoShape.Sine, 12, 50)
         .Track(0, 4, Lg).Chord(0, 0, 2, 85, 36, 43).Chord(0, 2, 2, 85, 34, 41);
        b.Syn(1, Structure.Pad2, 8, 32, FilterType.LowPass, 86, 25, 85, 95, 44, -12, 0, 85, 55, detune: 75).Track(1, 4, Lg)
         .Chord(1, 0, 1, 70, 60, 64, 67).Chord(1, 2, 1, 70, 59, 62, 66);
        b.Syn(2, Structure.Atmos2, 25, 24, FilterType.BandPass, 98, 30, 6, 100, 42, 16, 0, 95, 50,
            pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(3.0f), detune: 28, bal: 55).Track(2, 16, Hf)
         .Chord(2, 1, 1, 68, 84).Chord(2, 4, 1, 66, 88).Chord(2, 7, 1, 68, 79).Chord(2, 11, 1, 64, 91).Chord(2, 14, 1, 66, 86)
         .Prob(2, 1, 70).Prob(2, 4, 55).Prob(2, 7, 70).Prob(2, 11, 45).Prob(2, 14, 55);
        b.Noise(8, 100, FilterType.HighPass, 36, 90, 70, 18, 0, 80, 40).Track(3, 1, Mx).Chord(3, 0, 1, 70, 60);
        return b.P;
    }

    // 8. NIGHTFALL: dark Pythagorean (flat) minor drone with slow overdrive-warmed pad.
    private static PatternPalette Nightfall()
    {
        var b = new B("NIGHTFAL", 48, ReverbType.InfinityL, 92, MasterFxType.Overdrive, 40, 25, trimDb: -2.2f);   // RMS -21.8 -> -23.9 dBFS, peak -13.3 dBFS
        b.Temper(Temperament.PythagoreanFlat, 0, 0, 0, 0);
        b.Syn(0, Structure.Drone1, 3, 50, FilterType.LowPass, 56, 30, 100, 110, 60, 0, -2, 70, 0, detune: 82).Mod(0, LfoShape.Sine, 14, 55)
         .Track(0, 4, Lg).Chord(0, 0, 2, 90, 36, 43).Chord(0, 2, 2, 90, 31, 38);
        b.Syn(1, Structure.Pad1, 8, 24, FilterType.LowPass, 70, 28, 100, 105, 50, 0, -1, 90, 20, detune: 90).Mod(1, LfoShape.Triangle, 18, 60)
         .Track(1, 4, Lg).Chord(1, 0, 1, 70, 60, 63, 67).Chord(1, 1, 1, 70, 60, 63, 66).Chord(1, 2, 1, 70, 58, 62, 65).Chord(1, 3, 1, 70, 55, 58, 62);
        b.Syn(2, Structure.Atmos2, 3, 18, FilterType.BandPass, 84, 40, 20, 105, 30, -8, 0, 95, 30,
            pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(2.5f), detune: 40, bal: 40)
         .Track(2, 16, Hf).Chord(2, 6, 1, 60, 70).Chord(2, 13, 1, 58, 67).Prob(2, 6, 50).Prob(2, 13, 50);
        b.Noise(3, 126, FilterType.LowPass, 50, 100, 100, 40, 0, 40, 0).Track(3, 1, Mx).Chord(3, 0, 1, 90, 60);
        return b.P;
    }

    // 9. CRYSTAL: high, sparse FM bells and a shimmering pad; pure minor tuning.
    private static PatternPalette Crystal()
    {
        var b = new B("CRYSTAL", 84, ReverbType.InfinityH, 86, MasterFxType.TapeDelay, 66, 50, trimDb: 12f);   // RMS -36.0 -> -24.8 dBFS, peak -11.1 dBFS
        b.Temper(Temperament.PureMinor, 9, 9, 9, 9);
        b.Syn(0, Structure.Pad1, 8, 36, FilterType.LowPass, 84, 25, 90, 100, 44, 0, 0, 80, 60, detune: 76).Mod(0, LfoShape.Sine, 24, 45)
         .Track(0, 8, Wh).Chord(0, 0, 4, 70, 57, 60, 64).Chord(0, 4, 4, 70, 53, 57, 60);
        b.Syn(1, Structure.Atmos2, 25, 30, FilterType.HighPass, 40, 20, 4, 100, 38, -14, 1, 95, 80,
            pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(5.0f), detune: 24, bal: 70)
         .Track(1, 16, Qt).Chord(1, 0, 1, 64, 88).Chord(1, 3, 1, 60, 91).Chord(1, 6, 1, 64, 84).Chord(1, 10, 1, 60, 96).Chord(1, 13, 1, 62, 93)
         .Prob(1, 0, 60).Prob(1, 3, 70).Prob(1, 6, 60).Prob(1, 10, 50).Prob(1, 13, 60);
        b.Syn(2, Structure.Atmos2, 3, 24, FilterType.BandPass, 100, 30, 8, 100, 34, 16, 0, 95, 70,
            pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(7.0f), detune: 20, bal: 48)
         .Track(2, 12, Hf).Chord(2, 2, 1, 60, 81).Chord(2, 7, 1, 60, 76).Chord(2, 10, 1, 60, 84).Prob(2, 2, 70).Prob(2, 7, 70).Prob(2, 10, 70);
        b.Noise(8, 100, FilterType.HighPass, 60, 90, 80, 14, 0, 85, 55).Track(3, 1, Mx).Chord(3, 0, 1, 70, 60);
        return b.P;
    }

    // 10. RAINDAY: rain bed, soft pad, a slow plucked-ish pattern.
    private static PatternPalette RainDay()
    {
        var b = new B("RAINDAY", 68, ReverbType.SmallM, 66, MasterFxType.TapeDelay, 60, 38, trimDb: 3.9f);   // RMS -27.9 -> -24.0 dBFS, peak -11.2 dBFS
        b.Syn(0, Structure.Drone2, 3, 30, FilterType.LowPass, 64, 20, 85, 95, 46, -6, -1, 60, 0, detune: 70).Track(0, 4, Lg).Chord(0, 0, 2, 80, 36).Chord(0, 2, 2, 80, 43);
        b.Syn(1, Structure.Pad1, 8, 26, FilterType.LowPass, 84, 28, 70, 90, 46, 10, 0, 75, 25, detune: 72).Mod(1, LfoShape.Sine, 22, 40)
         .Track(1, 8, Br).Chord(1, 0, 1, 70, 60, 64, 67, 71).Chord(1, 2, 1, 70, 57, 60, 64, 67).Chord(1, 4, 1, 70, 53, 57, 60, 64).Chord(1, 6, 1, 70, 55, 59, 62, 65);
        b.Syn(2, Structure.Atmos2, 3, 22, FilterType.BandPass, 90, 35, 4, 80, 40, -10, 0, 80, 20,
            pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(2.0f), detune: 24, bal: 50)
         .Track(2, 16, E8).Chord(2, 0, 1, 70, 72).Chord(2, 3, 1, 62, 76).Chord(2, 6, 1, 66, 79).Chord(2, 8, 1, 70, 74).Chord(2, 11, 1, 60, 77).Chord(2, 14, 1, 64, 81)
         .Prob(2, 3, 70).Prob(2, 6, 70).Prob(2, 11, 60).Prob(2, 14, 60);
        b.Noise(1, 127, FilterType.Off, 100, 40, 60, 56, 0, 40, 0).Track(3, 1, Mx).Chord(3, 0, 1, 100, 60);
        return b.P;
    }

    // 11. BREATHE: a pad whose cutoff / harmonic move with step p-locks, like breathing.
    private static PatternPalette Breathe()
    {
        var b = new B("BREATHE", 60, ReverbType.LargeM, 80, MasterFxType.Chorus, 30, 45, trimDb: 5.1f);   // RMS -29.1 -> -23.8 dBFS, peak -9.5 dBFS
        b.Syn(0, Structure.Drone1, 3, 36, FilterType.LowPass, 64, 25, 90, 100, 54, 0, -1, 70, 0, detune: 74).Track(0, 8, Wh).Chord(0, 0, 8, 85, 36, 43);
        b.Syn(1, Structure.Pad2, 8, 20, FilterType.LowPass, 50, 30, 60, 85, 54, 0, 0, 80, 40, detune: 76).Mod(1, LfoShape.Sine, 28, 40)
         .Track(1, 8, Wh).Chord(1, 0, 8, 80, 60, 63, 67, 70);
        float[] cut = { 50, 62, 76, 92, 100, 92, 76, 62 }, har = { 20, 34, 50, 70, 90, 70, 50, 34 };
        for (int s = 0; s < 8; s++) b.Lock(1, s, ParamId.Cutoff, cut[s]).Lock(1, s, ParamId.Harmonic, har[s]);
        b.Syn(2, Structure.Atmos1, 3, 30, FilterType.LowPass, 80, 25, 60, 90, 34, 14, 1, 85, 50).Track(2, 8, Wh)
         .Chord(2, 2, 2, 70, 79).Chord(2, 6, 2, 70, 75).Prob(2, 2, 80).Prob(2, 6, 80);
        b.Noise(6, 104, FilterType.LowPass, 74, 80, 80, 30, 0, 60, 0).Lfo(3, 1, LfoShape.Sine, LfoAssign.Level, 0, 14, 70).Track(3, 1, Mx).Chord(3, 0, 1, 90, 60);
        return b.P;
    }

    // 12. POLYMETR: Eno-style loops of different lengths (3, 4, 5, 7 steps of 1/2): the combination only repeats after 420 steps.
    // (The two pad loops use 2-step ties and velocity 95-100: with short, soft notes the palette was 14 dB too quiet and would have needed more than the LEVEL ceiling.)
    private static PatternPalette Polymeter()
    {
        var b = new B("POLYMETR", 78, ReverbType.LargeM, 84, MasterFxType.TapeDelay, 58, 42, trimDb: 4.8f);   // RMS -38.3 -> -24.0 dBFS, peak -8.9 dBFS
        b.Syn(0, Structure.Pad1, 8, 28, FilterType.LowPass, 80, 25, 50, 90, 46, -14, 0, 80, 30, detune: 74).Track(0, 3, Hf).Chord(0, 0, 2, 100, 60).Chord(0, 1, 2, 95, 67);
        b.Syn(1, Structure.Pad1, 22, 44, FilterType.LowPass, 86, 25, 50, 90, 44, 14, 0, 80, 40, detune: 76).Track(1, 4, Hf).Chord(1, 0, 2, 95, 64).Chord(1, 2, 2, 95, 69);
        b.Syn(2, Structure.Atmos2, 25, 24, FilterType.BandPass, 94, 32, 6, 95, 36, 0, 1, 90, 50,
            pitch: Sambient.Dsp.Params.ParamCurves.Atmos2RatioToPitch(3.0f), detune: 26, bal: 52)
         .Track(2, 5, Hf).Chord(2, 0, 1, 66, 72).Chord(2, 2, 1, 62, 76).Chord(2, 3, 1, 64, 79).Prob(2, 2, 70).Prob(2, 3, 70);
        b.Noise(7, 110, FilterType.LowPass, 80, 70, 80, 36, 0, 60, 20).Track(3, 7, Hf).Chord(3, 0, 1, 80, 60)
         .Lock(3, 3, ParamId.Wave, 4).Lock(3, 5, ParamId.Wave, 6);
        return b.P;
    }
}
