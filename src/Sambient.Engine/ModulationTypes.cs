namespace Sambient.Engine;

/// <summary>BlendWave structures (see PLAN.md 4.2).</summary>
public enum Structure { Drone1, Drone2, Pad1, Pad2, Atmos1, Atmos2 }

/// <summary>The 24 LFO / MOD waveforms of the original, in display order.</summary>
public enum LfoShape
{
    Sine, Square, Triangle, Saw, ReverseSaw, Random, SmoothRandom, Log, ReverseLog,
    Pulse10, Pulse25, Pulse75, Pulse90, Step2, Step3, Step4, Step5, Step6, Step7,
    RampUp, RampDown, LowSine, LowTriangle, LowSmoothRandom,
}

/// <summary>LFO destinations. <see cref="Lfo1Rate"/>/<see cref="Lfo1Depth"/> only from LFO2; <see cref="OscLevel"/> only from LFO1.</summary>
public enum LfoAssign
{
    Off, Tune, Harmonic, Balance, Pitch, DetuneFeedback, Lfo1Rate, Lfo1Depth, ModRate, ModDepth,
    Cutoff, Resonance, Pan, Level, ShimmerSend, ReverbSend, OscLevel,
}

public enum VoiceMode { Poly, Mono, Legato, Unison, Arp }

/// <summary>Arpeggiator types: UP, DOWN, U.D, D.U, U.A.D, D.A.U, RNDM, UP+1, UP+2, DN-1, DN-2, P.O.</summary>
public enum ArpType { Up, Down, UpDown, DownUp, UpAndDown, DownAndUp, Random, UpPlus1, UpPlus2, DownMinus1, DownMinus2, PlayOrder }

/// <summary>Step note lengths (sequencer NOTE / arpeggiator speed), longest first.</summary>
public enum StepNote
{
    Maxima8_1, Longa4_1, Triple3_1, Breve2_1, Whole1_1, Half1_2, DottedQuarter, Quarter1_4,
    DottedEighth, HalfTriplet, Eighth1_8, DottedSixteenth, QuarterTriplet, Sixteenth1_16, ThirtySecond1_32,
}
