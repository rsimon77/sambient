using Sambient.Dsp.Modulation;
using Sambient.Dsp.Params;

namespace Sambient.Engine;

/// <summary>Static facts about the BlendWave structures (PLAN.md 4.2).</summary>
public static class StructureInfo
{
    /// <summary>Number of oscillators the structure renders per voice (ATMOS2: carrier + modulator).</summary>
    public static int OscillatorCount(Structure s) => s switch
    {
        Structure.Drone1 => 2,
        Structure.Drone2 => 2,
        Structure.Pad1 => 3,
        Structure.Pad2 => 3,
        Structure.Atmos1 => 1,
        Structure.Atmos2 => 2,
        _ => 1,
    };

    /// <summary>Oscillator cost for the (optional) 34-oscillator "classic budget": ATMOS1 counts as 3.</summary>
    public static int OscillatorCost(Structure s) => s == Structure.Atmos1 ? 3 : OscillatorCount(s);

    /// <summary>DRONE1/DRONE2: default octave -2 and the 20 s release range.</summary>
    public static bool IsDrone(Structure s) => s is Structure.Drone1 or Structure.Drone2;
}

/// <summary>Implemented by layers that own a <see cref="LayerModulation"/> (the engine reads their LFO send modulation).</summary>
internal interface IModulatedLayer
{
    LayerModulation Mod { get; }
}

/// <summary>
/// Layer-wide modulation state, shared by all voices of one layer (SynthLayer / NoiseLayer call
/// <see cref="Update"/> once per Render and <see cref="BeginSub"/> once per sub-block, before the voices run).
///
/// It (1) snapshots the structure / MOD / LFO1 / LFO2 parameters, (2) runs the layer-level LFO "clocks" that voices whose
/// LFO trigger is OFF (free-running) read per sample - so the phase continues across notes and all voices share it -
/// and (3) computes the layer-level send modulation (LFO -> SHIMMER / REVERB), published as offsets in knob units that the
/// <see cref="AudioEngine"/> adds to the send knobs. A clock is an <see cref="Lfo"/> with the layer's trigger mode that is
/// restarted by any voice start, so with trig INF/1..8 the send LFO restarts on every layer note-on.
///
/// LFO1/LFO2 destination ranges at depth 127 (depth/127 scales linearly unless noted):
/// TUNE +-12 st (depth^2 taper) | HARMONIC +-64 frames (per sample) | BALANCE +-63.5 knob | PITCH +-240 units (+-12 st / ratio) |
/// DT.FB +-63.5 knob | L1.RT x2^(+-2) (LFO2 only) | L1.DP +-63.5 knob (LFO2 only) | MOD RATE +-63.5 knob | MOD DEPTH +-63.5 knob |
/// CUTOFF +-63.5 knob | RESO +-63.5 knob | PAN +-63 | LEVEL tremolo gain 1 - d(1-u), u = (x+1)/2 (per sample) |
/// SHIMMER / REVERB send +-63.5 knob units, result clamped 0..127 | OSC LEVEL same as LEVEL but before the filter (LFO1 only).
///
/// Click safety: the LFO depths, the PITCH / DETUNE / BALANCE / MOD DEPTH knobs are smoothed (20 ms) here, and an LFO ASSIGN
/// change keeps the OLD destination alive as a second "tap" whose depth fades to 0 while the new tap's depth fades in from 0
/// (so a destination such as LEVEL never steps). Shape changes are morphed inside <see cref="Lfo"/>.
/// </summary>
internal sealed class LayerModulation
{
    /// <summary>Largest number of samples per sub-block the clocks and the voice modulators hold.</summary>
    public const int MaxSub = 128;

    private const float SmoothMs = 20f;

    private readonly int _index;
    private readonly ParameterStore _params;
    private Lfo _c1, _c2;
    private bool _noteFlag;
    private int _oscAcc;
    private bool _init;
    private SmoothedValue _sPitch, _sDetune, _sBalance, _sModDepth, _sD1, _sD2, _sD1Old, _sD2Old;

    public readonly bool IsNoise;

    // ---- parameter snapshot (refreshed by Update) ----
    public Structure Structure;
    /// <summary>Smoothed PITCH / DETUNE / BALANCE / MOD DEPTH knob values (updated per sub-block).</summary>
    public float PitchUnits, DetuneKnob, BalanceKnob, ModDepthKnob;
    public float ModRateKnob;
    public LfoWave ModShape;
    public LfoWave L1Shape, L2Shape;
    /// <summary>Current LFO destination (normalised: LFO1 never Lfo1Rate/Lfo1Depth, LFO2 never OscLevel) and its smoothed depth 0..127.</summary>
    public LfoAssign L1Assign, L2Assign;
    public float L1Depth, L2Depth;
    /// <summary>Destination the LFO had before its last ASSIGN change, fading out (Off = none) and its remaining depth.</summary>
    public LfoAssign L1OldAssign, L2OldAssign;
    public float L1OldDepth, L2OldDepth;
    public int L1Trig, L2Trig;
    public float L1Rate, L2Rate;
    public bool Lfo1Needed, Lfo2Needed;

    /// <summary>Free-running (trig OFF) LFO outputs of the current sub-block, bipolar, valid when the matching *Needed flag is set.</summary>
    public readonly float[] Clock1 = new float[MaxSub], Clock2 = new float[MaxSub];

    /// <summary>Layer-level LFO -> send modulation of the current sub-block, in send-knob units (add to the knob, clamp 0..127).</summary>
    public float ReverbSendOffset, ShimmerSendOffset;

    public bool LongRelease => !IsNoise && StructureInfo.IsDrone(Structure);

    /// <summary>Test hook: oscillators rendered by all voices in the most recent sub-block.</summary>
    public int OscillatorsRendered => _oscAcc;

    /// <summary>
    /// Neutral instance (single plain oscillator, no MOD, no LFOs) used by the legacy voice entry points
    /// (<c>VoiceBlock.Mod == null</c>) of layers that have not been migrated to <see cref="LayerModulation"/> yet.
    /// </summary>
    internal static readonly LayerModulation Neutral = CreateNeutral();

    private static LayerModulation CreateNeutral()
    {
        var p = new ParameterStore();
        p.Set(0, ParamId.Structure, (float)Structure.Atmos1);
        return new LayerModulation(0, 48000f, p);
    }

    public LayerModulation(int layerIndex, float sampleRate, ParameterStore parameters)
    {
        _index = layerIndex; _params = parameters;
        IsNoise = layerIndex == ParameterStore.NoiseLayerIndex;
        _c1 = new Lfo(sampleRate, (uint)(layerIndex * 2 + 101)) { MorphShapeChanges = true };
        _c2 = new Lfo(sampleRate, (uint)(layerIndex * 2 + 102)) { MorphShapeChanges = true };
        _sPitch = new SmoothedValue(0f, sampleRate, SmoothMs); _sDetune = new SmoothedValue(0f, sampleRate, SmoothMs);
        _sBalance = new SmoothedValue(0f, sampleRate, SmoothMs); _sModDepth = new SmoothedValue(0f, sampleRate, SmoothMs);
        _sD1 = new SmoothedValue(0f, sampleRate, SmoothMs); _sD2 = new SmoothedValue(0f, sampleRate, SmoothMs);
        _sD1Old = new SmoothedValue(0f, sampleRate, SmoothMs); _sD2Old = new SmoothedValue(0f, sampleRate, SmoothMs);
        Update();
    }

    /// <summary>Called by a voice when a note starts (restarts the layer-level clocks with trig INF / 1..8).</summary>
    public void NoteStarted() => _noteFlag = true;

    /// <summary>Called by voices: adds to the rendered-oscillator counter.</summary>
    public void CountOscillators(int n) => _oscAcc += n;

    public void Update()
    {
        int i = _index;
        var p = _params;
        Structure = (Structure)(int)Math.Clamp(p.Get(i, ParamId.Structure), 0f, 5f);
        ModRateKnob = p.Get(i, ParamId.ModRate);
        ModShape = (LfoWave)(int)p.Get(i, ParamId.ModShape);
        L1Shape = (LfoWave)(int)p.Get(i, ParamId.Lfo1Shape);
        L2Shape = (LfoWave)(int)p.Get(i, ParamId.Lfo2Shape);
        var a1 = (LfoAssign)(int)p.Get(i, ParamId.Lfo1Assign);
        var a2 = (LfoAssign)(int)p.Get(i, ParamId.Lfo2Assign);
        if (a1 is LfoAssign.Lfo1Rate or LfoAssign.Lfo1Depth) a1 = LfoAssign.Off;
        if (a2 == LfoAssign.OscLevel) a2 = LfoAssign.Off;
        L1Trig = (int)p.Get(i, ParamId.Lfo1Trig);
        L2Trig = (int)p.Get(i, ParamId.Lfo2Trig);
        L1Rate = p.Get(i, ParamId.Lfo1Rate);
        L2Rate = p.Get(i, ParamId.Lfo2Rate);

        float pitch = p.Get(i, ParamId.Pitch), det = p.Get(i, ParamId.Detune), bal = p.Get(i, ParamId.Balance), md = p.Get(i, ParamId.ModDepth);
        float d1 = p.Get(i, ParamId.Lfo1Depth), d2 = p.Get(i, ParamId.Lfo2Depth);
        if (!_init)
        {
            _init = true;
            _sPitch.SetImmediate(pitch); _sDetune.SetImmediate(det); _sBalance.SetImmediate(bal); _sModDepth.SetImmediate(md);
            L1Assign = a1; L2Assign = a2; L1OldAssign = L2OldAssign = LfoAssign.Off;
            _sD1.SetImmediate(d1); _sD2.SetImmediate(d2); _sD1Old.SetImmediate(0f); _sD2Old.SetImmediate(0f);
            Publish();
            return;
        }
        _sPitch.SetTarget(pitch); _sDetune.SetTarget(det); _sBalance.SetTarget(bal); _sModDepth.SetTarget(md);
        SwitchTap(a1, d1, ref L1Assign, ref L1OldAssign, ref _sD1, ref _sD1Old);
        SwitchTap(a2, d2, ref L2Assign, ref L2OldAssign, ref _sD2, ref _sD2Old);
    }

    /// <summary>An ASSIGN change fades the old destination out and the new one in; a depth change just ramps.</summary>
    private static void SwitchTap(LfoAssign target, float depth, ref LfoAssign cur, ref LfoAssign old,
        ref SmoothedValue sCur, ref SmoothedValue sOld)
    {
        if (target != cur)
        {
            old = cur;
            sOld.SetImmediate(sCur.Current);
            sOld.SetTarget(0f);
            cur = target;
            sCur.SetImmediate(0f);
        }
        sCur.SetTarget(depth);
    }

    private void Publish()
    {
        PitchUnits = _sPitch.Current; DetuneKnob = _sDetune.Current; BalanceKnob = _sBalance.Current; ModDepthKnob = _sModDepth.Current;
        L1Depth = _sD1.Current; L2Depth = _sD2.Current; L1OldDepth = _sD1Old.Current; L2OldDepth = _sD2Old.Current;
        if (L1OldAssign != LfoAssign.Off && L1OldDepth <= 0f && !_sD1Old.IsSmoothing) L1OldAssign = LfoAssign.Off;
        if (L2OldAssign != LfoAssign.Off && L2OldDepth <= 0f && !_sD2Old.IsSmoothing) L2OldAssign = LfoAssign.Off;
        bool l2Cur = L2Assign != LfoAssign.Off && L2Depth > 0f, l2Old = L2OldAssign != LfoAssign.Off && L2OldDepth > 0f;
        Lfo2Needed = l2Cur || l2Old;
        bool byL2 = (l2Cur && L2Assign == LfoAssign.Lfo1Depth) || (l2Old && L2OldAssign == LfoAssign.Lfo1Depth);
        Lfo1Needed = (L1Assign != LfoAssign.Off && (L1Depth > 0f || byL2)) || (L1OldAssign != LfoAssign.Off && (L1OldDepth > 0f || byL2));
    }

    /// <summary>
    /// LFO1 rate in Hz including the LFO2 -> L1.RT modulation (LFO2 value <paramref name="v2"/>, depths 0..1 of the current /
    /// fading-out LFO2 destination <paramref name="d2"/> / <paramref name="d2Old"/>).
    /// </summary>
    public float Lfo1RateHz(float v2, float d2, float d2Old)
    {
        float hz = ParamCurves.LfoRateHz(L1Rate);
        float e = 0f;
        if (L2Assign == LfoAssign.Lfo1Rate) e += d2;
        if (L2OldAssign == LfoAssign.Lfo1Rate) e += d2Old;
        if (e > 0f) hz *= MathF.Pow(2f, 2f * e * v2);
        return hz;
    }

    /// <summary>Effective depth 0..1 of an LFO1 tap with base depth <paramref name="baseDepth"/> (0..127) including the LFO2 -> L1.DP modulation.</summary>
    public float Lfo1Depth01(float baseDepth, float v2, float d2, float d2Old)
    {
        float d = baseDepth;
        if (L2Assign == LfoAssign.Lfo1Depth) d += 63.5f * d2 * v2;
        if (L2OldAssign == LfoAssign.Lfo1Depth) d += 63.5f * d2Old * v2;
        return Math.Clamp(d, 0f, 127f) * (1f / 127f);
    }

    /// <summary>Advances the smoothers and the layer-level clocks by <paramref name="n"/> samples and computes the send modulation.</summary>
    public void BeginSub(int n)
    {
        _oscAcc = 0;
        if (n > MaxSub) n = MaxSub;
        _sPitch.Skip(n); _sDetune.Skip(n); _sBalance.Skip(n); _sModDepth.Skip(n);
        _sD1.Skip(n); _sD2.Skip(n); _sD1Old.Skip(n); _sD2Old.Skip(n);
        Publish();
        if (_noteFlag) { _noteFlag = false; _c1.Retrigger(); _c2.Retrigger(); }

        float v2 = 0f, d2 = 0f, d2o = 0f;
        if (Lfo2Needed)
        {
            _c2.Configure(L2Shape, ParamCurves.LfoRateHz(L2Rate), L2Trig);
            _c2.Fill(Clock2.AsSpan(0, n));
            v2 = Clock2[n >> 1];
            d2 = L2Depth * (1f / 127f); d2o = L2OldDepth * (1f / 127f);
        }
        float v1 = 0f, d1 = 0f, d1o = 0f;
        if (Lfo1Needed)
        {
            _c1.Configure(L1Shape, Lfo1RateHz(v2, d2, d2o), L1Trig);
            _c1.Fill(Clock1.AsSpan(0, n));
            v1 = Clock1[n >> 1];
            d1 = Lfo1Depth01(L1Depth, v2, d2, d2o);
            d1o = Lfo1Depth01(L1OldDepth, v2, d2, d2o);
        }

        float rev = 0f, shim = 0f;
        if (Lfo1Needed) { Send(L1Assign, d1, v1, ref rev, ref shim); Send(L1OldAssign, L1OldAssign == LfoAssign.Off ? 0f : d1o, v1, ref rev, ref shim); }
        if (Lfo2Needed) { Send(L2Assign, d2, v2, ref rev, ref shim); Send(L2OldAssign, d2o, v2, ref rev, ref shim); }
        ReverbSendOffset = rev; ShimmerSendOffset = shim;
    }

    private static void Send(LfoAssign a, float d, float v, ref float rev, ref float shim)
    {
        if (a == LfoAssign.ReverbSend) rev += 63.5f * d * v;
        else if (a == LfoAssign.ShimmerSend) shim += 63.5f * d * v;
    }
}

/// <summary>
/// Per-voice modulation: the MOD LFO and the voice's own LFO1 / LFO2 instances (used when the LFO trigger is not OFF,
/// i.e. restarted on note-on), plus the resolution of all LFO destinations into per-sub-block scalars and per-sample spans.
/// Voices call <see cref="Retrigger"/> on note-on and <see cref="Prepare"/> once per chunk.
/// </summary>
internal sealed class VoiceModulator
{
    public const int Cap = LayerModulation.MaxSub;

    private Lfo _mod, _l1, _l2;
    private bool _rm, _r1, _r2;
    private readonly float[] _l1buf = new float[Cap], _l2buf = new float[Cap];

    // ---- results of Prepare ----
    /// <summary>MOD LFO output (bipolar) per sample; valid when <see cref="ModD"/> &gt; 0.</summary>
    public readonly float[] Mod = new float[Cap];
    /// <summary>Per-sample HARMONIC offset in frames (valid when <see cref="HasHarm"/>).</summary>
    public readonly float[] Harm = new float[Cap];
    /// <summary>Per-sample LEVEL gain 0..1 (valid when <see cref="HasLvl"/>) and OSC LEVEL gain (when <see cref="HasOscLvl"/>).</summary>
    public readonly float[] Lvl = new float[Cap], OscLvl = new float[Cap];
    public bool HasHarm, HasLvl, HasOscLvl;
    /// <summary>MOD depth 0..1 (knob + LFO MOD DEPTH offset).</summary>
    public float ModD;
    /// <summary>Block-level offsets: TUNE in semitones; PITCH in PITCH units; DT.FB / BALANCE / CUTOFF / RESO in knob units; PAN in pan units.</summary>
    public float Tune, Pitch, DtFb, Balance, Cutoff, Reso, Pan;

    public VoiceModulator(float sampleRate, uint seed)
    {
        _mod = new Lfo(sampleRate, seed * 3 + 1);
        _l1 = new Lfo(sampleRate, seed * 3 + 2);
        _l2 = new Lfo(sampleRate, seed * 3 + 3);
    }

    /// <summary>Note-on: restart the MOD LFO and (for trig INF / 1..8) the voice's LFO1 / LFO2 on their next use.</summary>
    public void Retrigger() { _rm = _r1 = _r2 = true; }

    /// <summary>Resolves the modulation for <paramref name="n"/> samples starting at sample <paramref name="offset"/> of the layer sub-block.</summary>
    public void Prepare(LayerModulation m, int n, int offset)
    {
        Tune = Pitch = DtFb = Balance = Cutoff = Reso = Pan = 0f;
        HasHarm = HasLvl = HasOscLvl = false;
        ModD = 0f;
        float modRateOff = 0f, modDepthOff = 0f;
        int mid = n >> 1;

        // LFO2 first (LFO1 rate/depth may depend on it)
        float v2 = 0f, d2 = 0f, d2o = 0f; float[]? b2 = null; int o2 = 0;
        if (m.Lfo2Needed)
        {
            d2 = m.L2Depth * (1f / 127f); d2o = m.L2OldDepth * (1f / 127f);
            if (m.L2Trig == Lfo.TrigOff) { b2 = m.Clock2; o2 = offset; }
            else
            {
                _l2.Configure(m.L2Shape, ParamCurves.LfoRateHz(m.L2Rate), m.L2Trig);
                if (_r2) { _l2.Retrigger(); _r2 = false; }
                _l2.Fill(_l2buf.AsSpan(0, n));
                b2 = _l2buf;
            }
            // a switch between the shared free-running clock and this voice's own LFO (TRIG change) morphs instead of stepping
            b2 = Morph(ref _sw2, b2, ref o2, _l2mix, n, m.L2Trig == Lfo.TrigOff);
            v2 = b2[o2 + mid];
        }
        else _sw2.Reset();
        float v1 = 0f, d1 = 0f, d1o = 0f; float[]? b1 = null; int o1 = 0;
        if (m.Lfo1Needed)
        {
            if (m.L1Trig == Lfo.TrigOff) { b1 = m.Clock1; o1 = offset; }
            else
            {
                _l1.Configure(m.L1Shape, m.Lfo1RateHz(v2, d2, d2o), m.L1Trig);
                if (_r1) { _l1.Retrigger(); _r1 = false; }
                _l1.Fill(_l1buf.AsSpan(0, n));
                b1 = _l1buf;
            }
            b1 = Morph(ref _sw1, b1, ref o1, _l1mix, n, m.L1Trig == Lfo.TrigOff);
            v1 = b1[o1 + mid];
            d1 = m.Lfo1Depth01(m.L1Depth, v2, d2, d2o);
            d1o = m.Lfo1Depth01(m.L1OldDepth, v2, d2, d2o);
        }
        else _sw1.Reset();

        if (b1 != null)
        {
            Apply(m.L1Assign, d1, b1, o1, v1, n, isLfo1: true, ref modRateOff, ref modDepthOff);
            if (m.L1OldAssign != LfoAssign.Off) Apply(m.L1OldAssign, d1o, b1, o1, v1, n, isLfo1: true, ref modRateOff, ref modDepthOff);
        }
        if (b2 != null)
        {
            Apply(m.L2Assign, d2, b2, o2, v2, n, isLfo1: false, ref modRateOff, ref modDepthOff);
            if (m.L2OldAssign != LfoAssign.Off) Apply(m.L2OldAssign, d2o, b2, o2, v2, n, isLfo1: false, ref modRateOff, ref modDepthOff);
        }

        // MOD LFO (always retriggered on note-on)
        float md = Math.Clamp(m.ModDepthKnob + modDepthOff, 0f, 127f) * (1f / 127f);
        if (md > 0f)
        {
            bool ring = !m.IsNoise && m.Structure == Structure.Atmos1;
            float rk = Math.Clamp(m.ModRateKnob + modRateOff, 0f, 127f);
            _mod.Configure(m.ModShape, ParamCurves.ModRateHz(rk, ring), Lfo.TrigInf);
            if (_rm) { _mod.Retrigger(); _rm = false; }
            _mod.Fill(Mod.AsSpan(0, n));
            ModD = md;
        }
    }

    /// <summary>Tracks which source (shared clock / own LFO) feeds an LFO slot and its last output, for the morph on a source change.</summary>
    private struct SourceSwitch
    {
        public int Mode;          // 0 = unknown, 1 = clock, 2 = own LFO
        public float Last;
        public int Left;
        public float From;
        public void Reset() { Mode = 0; Left = 0; }
    }

    private SourceSwitch _sw1, _sw2;
    private readonly float[] _l1mix = new float[Cap], _l2mix = new float[Cap];
    private const int SourceMorphSamples = 720;      // 15 ms at 48 kHz (block-rate use, only on a TRIG change)

    private static float[] Morph(ref SourceSwitch sw, float[] buf, ref int off, float[] scratch, int n, bool clock)
    {
        int mode = clock ? 1 : 2;
        if (sw.Mode != 0 && sw.Mode != mode) { sw.Left = SourceMorphSamples; sw.From = sw.Last; }
        sw.Mode = mode;
        if (sw.Left > 0)
        {
            for (int i = 0; i < n; i++)
            {
                float w = sw.Left > 0 ? 1f - (float)sw.Left / SourceMorphSamples : 1f;
                scratch[i] = sw.From + (buf[off + i] - sw.From) * w;
                if (sw.Left > 0) sw.Left--;
            }
            sw.Last = scratch[n - 1];
            off = 0;
            return scratch;
        }
        sw.Last = buf[off + n - 1];
        return buf;
    }

    private void Apply(LfoAssign a, float d, float[] buf, int o, float v, int n, bool isLfo1,
        ref float modRateOff, ref float modDepthOff)
    {
        switch (a)
        {
            case LfoAssign.Tune: Tune += 12f * d * d * v; break;
            case LfoAssign.Balance: Balance += 63.5f * d * v; break;
            case LfoAssign.Pitch: Pitch += 240f * d * v; break;
            case LfoAssign.DetuneFeedback: DtFb += 63.5f * d * v; break;
            case LfoAssign.ModRate: modRateOff += 63.5f * d * v; break;
            case LfoAssign.ModDepth: modDepthOff += 63.5f * d * v; break;
            case LfoAssign.Cutoff: Cutoff += 63.5f * d * v; break;
            case LfoAssign.Resonance: Reso += 63.5f * d * v; break;
            case LfoAssign.Pan: Pan += 63f * d * v; break;
            case LfoAssign.Harmonic:
            {
                float k = 64f * d;
                if (!HasHarm) { Array.Clear(Harm, 0, n); HasHarm = true; }
                for (int i = 0; i < n; i++) Harm[i] += k * buf[o + i];
                break;
            }
            case LfoAssign.Level:
            {
                if (!HasLvl) { Array.Fill(Lvl, 1f, 0, n); HasLvl = true; }
                float h = 0.5f * d;
                for (int i = 0; i < n; i++) Lvl[i] *= 1f - h * (1f - buf[o + i]);
                break;
            }
            case LfoAssign.OscLevel when isLfo1:
            {
                if (!HasOscLvl) { Array.Fill(OscLvl, 1f, 0, n); HasOscLvl = true; }
                float h = 0.5f * d;
                for (int i = 0; i < n; i++) OscLvl[i] *= 1f - h * (1f - buf[o + i]);
                break;
            }
        }
    }
}
