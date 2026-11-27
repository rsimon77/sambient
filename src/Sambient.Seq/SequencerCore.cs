using Sambient.Engine;

namespace Sambient.Seq;

public enum TransportState { Stopped = 0, PreCount = 1, Playing = 2 }

public enum SeqTapKind { NoteOn, NoteOff, Step, CycleBoundary, PaletteSwitch, FadeOutStart, FadeOutEnd, Started, Stopped, Beat }

/// <summary>Diagnostic / test event, delivered on the audio thread (keep the handler allocation-free).</summary>
public readonly record struct SeqTapEvent(long Sample, SeqTapKind Kind, int Track, int Step, int Note, int Velocity);

/// <summary>
/// The audio-thread half of the sequencer (see <see cref="Sequencer"/> for the UI half). All state here is touched only by
/// <see cref="AudioEngine.Render"/>; other threads talk to it through <see cref="Post"/> (a lock-free command queue), the
/// volatile settings fields, and the published (Volatile) position fields. Nothing here allocates after construction.
///
/// Timing: every track keeps a double sample countdown to its next step boundary (fractional samples are carried, so a step
/// of N.5 samples never drifts; events land within one sample of the exact time) and a list of pending note-offs. The engine
/// splits its blocks at <see cref="SamplesToNextEvent"/>, so events fire at their exact frame.
///
/// Cycle: the pattern cycle is the cycle of the longest track (the "master" track: ties go to the lowest index), or the explicit
/// PatternBars when set. Transpose, queued palette switches, chain repeat counting and Relaxation fades happen at its boundary.
/// </summary>
internal sealed class SequencerCore : IEngineSequencer
{
    private const int Tracks = SeqLimits.Tracks, MaxSteps = SeqLimits.MaxSteps, PendCap = 1024;
    private const double Eps = 1e-6;
    private const double KillSeconds = 0.25, DefaultGate = 0.98;
    private const float KillReleaseKnob = 3f;

    /// <summary>Quarter notes per step for each <see cref="StepNote"/> (double precision, so triplets land on the exact sample).</summary>
    internal static readonly double[] StepQuarters = [32, 16, 12, 8, 4, 2, 1.5, 1, 0.75, 4.0 / 3, 0.5, 0.375, 2.0 / 3, 0.25, 0.125];

    private ISequencerHost _host = null!;
    private ParameterStore _p = null!;
    private double _fs = 48000;
    private readonly MpscQueue<SeqCommand> _cmds = new(256);

    // ---- settings (written by any thread)
    private volatile bool _metronomeOn;
    private int _metronomeVolume = 8;
    private volatile bool _bpmFromPalette = true;
    private volatile bool _resetReverb = true;
    internal bool MetronomeOn { get => _metronomeOn; set => _metronomeOn = value; }
    internal int MetronomeVolume { get => Volatile.Read(ref _metronomeVolume); set => Volatile.Write(ref _metronomeVolume, Math.Clamp(value, 0, 15)); }
    /// <summary>PTN BPM mode (true): the palette's BPM is loaded on select; GLBL (false): the current BPM is kept.</summary>
    internal bool BpmFromPalette { get => _bpmFromPalette; set => _bpmFromPalette = value; }
    /// <summary>R.RST: reset the reverb tail on every palette change.</summary>
    internal bool ResetReverbOnChange { get => _resetReverb; set => _resetReverb = value; }
    public Action<SeqTapEvent>? Tap;

    // ---- active material
    private PreparedPalette? _active, _queued;
    private ChainPlan? _chain;
    private int _chainIdx, _cycleCount;
    private int _transpose;

    // ---- transport
    private TransportState _state;
    private int _preTotal, _preDone, _beatIdx;
    private double _beatToNext;
    private double _barToNext;
    private long _abs;
    private int _master;
    private bool _killActive;
    private double _killLeft;
    private uint _rng = 0x2545F491u;

    // ---- per track
    private readonly bool[] _running = new bool[Tracks];
    private readonly double[] _toNext = new double[Tracks], _curLen = new double[Tracks];
    private readonly int[] _played = new int[Tracks], _curStep = new int[Tracks], _blk = new int[Tracks];
    private readonly long[] _absSteps = new long[Tracks];
    private readonly bool[] _hasOvr = new bool[Tracks];
    private readonly byte[][] _pendNote = new byte[Tracks][];
    private readonly double[][] _pendLeft = new double[Tracks][];
    private readonly int[] _pendCount = new int[Tracks];

    // ---- relaxation fade
    private enum Fade { None, Out, In }
    private Fade _fade;
    private double _fadeLen, _fadeLeft;

    // ---- metronome click
    private float _clickPhase, _clickEnv, _clickFreq;

    // ---- published (audio -> any thread)
    private int _pubState, _pubCycle, _pubChainIdx, _pubPre;
    private readonly int[] _pubStep = new int[Tracks];
    private readonly long[] _pubAbsBits = new long[Tracks];
    private PreparedPalette? _pubActive;
    private int _pubFade;               // 0 none, 1 out, 2 in
    private int _pubFadeGainBits;
    private long _pubSample;

    public SequencerCore()
    {
        for (int t = 0; t < Tracks; t++) { _pendNote[t] = new byte[PendCap]; _pendLeft[t] = new double[PendCap]; _curStep[t] = -1; _pubStep[t] = -1; }
        _pubFadeGainBits = BitConverter.SingleToInt32Bits(1f);
    }

    public void Bind(ISequencerHost host)
    {
        _host = host; _p = host.Parameters; _fs = host.SampleRate;
    }

    // ------------------------------------------------------------------ published state (any thread)

    internal TransportState State => (TransportState)Volatile.Read(ref _pubState);
    internal int PreCountLeft => Volatile.Read(ref _pubPre);
    internal int CycleCount => Volatile.Read(ref _pubCycle);
    internal int ChainIndex => Volatile.Read(ref _pubChainIdx);
    internal PreparedPalette? ActivePalette => Volatile.Read(ref _pubActive);
    internal int FadePhase => Volatile.Read(ref _pubFade);
    internal float FadeGain => BitConverter.Int32BitsToSingle(Volatile.Read(ref _pubFadeGainBits));
    internal long SamplePosition => Volatile.Read(ref _pubSample);
    internal long DroppedCommands => _cmds.Dropped;
    internal int CurrentStep(int track) => Volatile.Read(ref _pubStep[track]);
    /// <summary>Continuous position of the track in steps since play: (steps started - 1) + phase of the current step.</summary>
    internal double AbsPosition(int track) => BitConverter.Int64BitsToDouble(Volatile.Read(ref _pubAbsBits[track]));
    /// <summary>Steps started on the track since play (monotonic, also with RND SEQ).</summary>
    internal long StepsStarted(int track) => (long)Math.Floor(AbsPosition(track)) + 1;

    internal bool Post(in SeqCommand c) => _cmds.TryEnqueue(in c);

    // ------------------------------------------------------------------ IEngineSequencer

    public void BeginBlock(int frames)
    {
        while (_cmds.TryDequeue(out var c)) Execute(in c);
    }

    public int SamplesToNextEvent
    {
        get
        {
            double m = double.MaxValue;
            if (_killActive) m = Math.Min(m, _killLeft);
            if (_fade != Fade.None) m = Math.Min(m, _fadeLeft);
            if (_state != TransportState.Stopped)
            {
                m = Math.Min(m, _beatToNext);
                if (_state == TransportState.Playing)
                {
                    if (_active != null && _active.Pattern.PatternBars > 0) m = Math.Min(m, _barToNext);
                    for (int t = 0; t < Tracks; t++)
                    {
                        if (!_running[t]) continue;
                        m = Math.Min(m, _toNext[t]);
                        var pl = _pendLeft[t];
                        for (int i = 0; i < _pendCount[t]; i++) m = Math.Min(m, pl[i]);
                    }
                }
            }
            if (m == double.MaxValue) return int.MaxValue;
            double c = Math.Ceiling(m - Eps);
            return c <= 0 ? 0 : c >= int.MaxValue ? int.MaxValue : (int)c;
        }
    }

    public void Advance(int n)
    {
        if (_killActive) _killLeft -= n;
        if (_fade != Fade.None) _fadeLeft -= n;
        if (_state != TransportState.Stopped)
        {
            _beatToNext -= n;
            _barToNext -= n;
            for (int t = 0; t < Tracks; t++)
            {
                if (!_running[t]) continue;
                _toNext[t] -= n;
                var pl = _pendLeft[t];
                for (int i = 0; i < _pendCount[t]; i++) pl[i] -= n;
            }
        }
        _abs += n;
        Publish();
    }

    public void ProcessDueEvents()
    {
        if (_killActive && _killLeft <= Eps)
        {
            _killActive = false;
            for (int l = 0; l < Tracks; l++) _p.ClearOverride(l, ParamId.Release);
        }
        if (_fade != Fade.None && _fadeLeft <= Eps) OnFadeEnd();
        if (_state == TransportState.Stopped) return;

        while (_state != TransportState.Stopped && _beatToNext <= Eps) OnBeat();
        if (_state != TransportState.Playing) return;

        var pat = _active!.Pattern;
        if (pat.PatternBars > 0)
        {
            while (_state == TransportState.Playing && _barToNext <= Eps) { OnCycleBoundary(); _barToNext += BarSamples(pat.PatternBars); }
        }
        else if (_running[_master] && _toNext[_master] <= Eps && _played[_master] >= Len(_master))
        {
            OnCycleBoundary();
        }
        if (_state != TransportState.Playing) return;

        for (int t = 0; t < Tracks; t++)
        {
            var pl = _pendLeft[t]; var pn = _pendNote[t];
            for (int i = 0; i < _pendCount[t];)
            {
                if (pl[i] <= Eps)
                {
                    Emit(SeqTapKind.NoteOff, t, _curStep[t], pn[i], 0);
                    _host.SeqNoteOff(t, pn[i]);
                    int last = --_pendCount[t];
                    pl[i] = pl[last]; pn[i] = pn[last];
                }
                else i++;
            }
            while (_running[t] && _toNext[t] <= Eps) FireStep(t);
        }
    }

    public void MixPost(Span<float> st, int frames)
    {
        if (_fade != Fade.None)
        {
            for (int i = 0; i < frames; i++)
            {
                double prog = 1.0 - Math.Clamp((_fadeLeft - i) / _fadeLen, 0.0, 1.0);
                float g = _fade == Fade.Out ? (float)Math.Cos(prog * Math.PI * 0.5) : (float)Math.Sin(prog * Math.PI * 0.5);
                st[2 * i] *= g; st[2 * i + 1] *= g;
            }
        }
        if (_clickEnv > 1e-4f)
        {
            int vol = MetronomeVolume;
            float amp = 0.5f * (vol / 15f) * (vol / 15f);
            float inc = (float)(2 * Math.PI * _clickFreq / _fs);
            float decay = (float)Math.Exp(-1.0 / (0.012 * _fs));
            for (int i = 0; i < frames && _clickEnv > 1e-4f; i++)
            {
                float s = MathF.Sin(_clickPhase) * _clickEnv * amp;
                _clickPhase += inc;
                if (_clickPhase > MathF.PI * 2) _clickPhase -= MathF.PI * 2;
                _clickEnv *= decay;
                st[2 * i] += s; st[2 * i + 1] += s;
            }
        }
    }

    // ------------------------------------------------------------------ commands

    private void Execute(in SeqCommand c)
    {
        switch (c.Type)
        {
            case CmdType.Play: StartTransport(c.A); break;
            case CmdType.Stop: StopTransport(kill: true); break;
            case CmdType.StopNatural: StopTransport(kill: false); break;
            case CmdType.Load:
                if (c.Palette != null) { _queued = null; _chain = null; _fade = Fade.None; SwitchTo(c.Palette); }
                break;
            case CmdType.Adopt:
                if (c.Palette != null) { _active = c.Palette; Volatile.Write(ref _pubActive, c.Palette); }    // make it the active pattern without touching parameters
                break;
            case CmdType.Queue:
                if (c.Palette != null)
                {
                    if (_state == TransportState.Stopped || _active == null) { _queued = null; SwitchTo(c.Palette); }
                    else _queued = c.Palette;
                }
                break;
            case CmdType.SetChain:
                _chain = c.Chain; _chainIdx = Math.Clamp(c.A, 0, Math.Max(0, (c.Chain?.Items.Length ?? 1) - 1)); _cycleCount = 0;
                Volatile.Write(ref _pubChainIdx, _chainIdx);
                break;
            case CmdType.ClearChain:
                _chain = null; _fade = Fade.None; Volatile.Write(ref _pubFade, 0);
                break;
            case CmdType.LiveParam:
                _p.SetOverride(c.A, (ParamId)c.B, c.F);
                if ((uint)c.A < Tracks) _hasOvr[c.A] = true;        // so the next step boundary restores it
                break;
            case CmdType.Seed: _rng = c.A == 0 ? 1u : (uint)c.A; break;
        }
        Publish();
    }

    private void StartTransport(int preBeats)
    {
        if (_active == null) return;
        for (int t = 0; t < Tracks; t++) { ReleasePending(t); _running[t] = false; _p.ClearOverrides(t); _hasOvr[t] = false; }
        _killActive = false;
        _beatToNext = 0; _beatIdx = 0;
        preBeats = Math.Clamp(preBeats, 0, 8);
        _preTotal = preBeats; _preDone = 0;
        if (preBeats > 0) _state = TransportState.PreCount;
        else { _state = TransportState.Playing; StartTracks(); }
        Emit(SeqTapKind.Started, -1, -1, 0, 0);
    }

    private void StartTracks()
    {
        _transpose = (int)MathF.Round(_p.Get(GlobalParamId.Transpose));
        _cycleCount = 0;
        Volatile.Write(ref _pubCycle, 0);
        for (int t = 0; t < Tracks; t++)
        {
            _running[t] = true; _toNext[t] = 0; _played[t] = 0; _curStep[t] = -1; _absSteps[t] = 0; _curLen[t] = 0; _blk[t] = 0;
        }
        ChooseMaster();
        int bars = _active!.Pattern.PatternBars;
        _barToNext = bars > 0 ? BarSamples(bars) : double.MaxValue;
    }

    private void StopTransport(bool kill)
    {
        for (int t = 0; t < Tracks; t++)
        {
            ReleasePending(t); _running[t] = false;
            _p.ClearOverrides(t); _hasOvr[t] = false;
        }
        bool was = _state != TransportState.Stopped;
        _state = TransportState.Stopped;
        _preDone = _preTotal = 0;
        _cycleCount = 0;
        if (kill) KillVoices();
        if (was) Emit(SeqTapKind.Stopped, -1, -1, 0, 0);
    }

    private void KillVoices()
    {
        _host.ReleaseAllNotes();
        for (int l = 0; l < Tracks; l++) _p.SetOverride(l, ParamId.Release, KillReleaseKnob);   // ~5 ms release: click-free but immediate
        _killActive = true; _killLeft = KillSeconds * _fs;
    }

    private void ReleasePending(int t)
    {
        for (int i = 0; i < _pendCount[t]; i++) { Emit(SeqTapKind.NoteOff, t, _curStep[t], _pendNote[t][i], 0); _host.SeqNoteOff(t, _pendNote[t][i]); }
        _pendCount[t] = 0;
    }

    /// <summary>Makes <paramref name="p"/> the active palette: loads its base parameters and restarts the tracks (if playing).</summary>
    private void SwitchTo(PreparedPalette p)
    {
        for (int t = 0; t < Tracks; t++) { ReleasePending(t); _p.ClearOverrides(t); _hasOvr[t] = false; }
        int n = ParamTable.LayerParamCount;
        for (int l = 0; l < Tracks; l++)
            for (int k = 0; k < n; k++) _p.Set(l, (ParamId)k, p.LayerBase[l * n + k]);
        foreach (var g in PatternPalette.PatternGlobals)
        {
            if (g == GlobalParamId.Bpm && !_bpmFromPalette) continue;
            _p.Set(g, p.GlobalBase[(int)g]);
        }
        if (_resetReverb) _host.ResetReverb();
        _active = p; _queued = null;
        _cycleCount = 0;
        _transpose = (int)MathF.Round(_p.Get(GlobalParamId.Transpose));
        if (_state == TransportState.Playing) StartTracks();
        Volatile.Write(ref _pubActive, p);
        Emit(SeqTapKind.PaletteSwitch, -1, -1, p.Slot, 0);
    }

    // ------------------------------------------------------------------ clock events

    private void OnBeat()
    {
        double beat = 60.0 / Math.Max(1f, _p.Get(GlobalParamId.Bpm)) * _fs;
        bool accent = (_beatIdx & 3) == 0;
        if (_state == TransportState.PreCount)
        {
            if (_preDone >= _preTotal)
            {
                _state = TransportState.Playing; StartTracks(); _beatIdx = 0; accent = true;
                if (_metronomeOn) Click(accent);
            }
            else
            {
                Click(accent); _preDone++;
                Volatile.Write(ref _pubPre, _preTotal - _preDone);
            }
        }
        else if (_metronomeOn) Click(accent);
        Emit(SeqTapKind.Beat, -1, _beatIdx, 0, 0);
        _beatIdx++;
        _beatToNext += beat;
    }

    private void Click(bool accent)
    {
        if (MetronomeVolume == 0) return;
        _clickEnv = 1f; _clickPhase = 0f; _clickFreq = accent ? 1500f : 1000f;
    }

    private double BarSamples(int bars) => bars * 4.0 * 60.0 / Math.Max(1f, _p.Get(GlobalParamId.Bpm)) * _fs;

    private int Len(int t) => Math.Clamp((int)MathF.Round(_p.Get(t, ParamId.SeqLength)), 1, MaxSteps);

    private double StepSamples(int t)
    {
        int sn = Math.Clamp((int)MathF.Round(_p.Get(t, ParamId.StepNote)), 0, StepQuarters.Length - 1);
        return StepQuarters[sn] * 60.0 / Math.Max(1f, _p.Get(GlobalParamId.Bpm)) * _fs;
    }

    private void ChooseMaster()
    {
        int best = 0; double bd = -1;
        for (int t = 0; t < Tracks; t++)
        {
            double d = Len(t) * StepSamples(t);
            if (d > bd + 0.5) { bd = d; best = t; }
        }
        _master = best;
    }

    // ------------------------------------------------------------------ cycle / palette logic

    private void OnCycleBoundary()
    {
        _cycleCount++;
        Volatile.Write(ref _pubCycle, _cycleCount);
        Emit(SeqTapKind.CycleBoundary, -1, -1, _cycleCount, 0);
        if (_pubActive != _active) Volatile.Write(ref _pubActive, _active);

        if (_queued != null) { SwitchTo(_queued); return; }
        var ch = _chain;
        if (ch != null && _cycleCount >= Math.Max(1, ch.Repeats))
        {
            if (ch.Relaxation)
            {
                if (_fade == Fade.None) BeginFade(Fade.Out, ch.FadeSeconds);
            }
            else { AdvanceChain(); return; }
        }
        _transpose = (int)MathF.Round(_p.Get(GlobalParamId.Transpose));   // transpose takes effect at the start of the next cycle
        if (_active!.Pattern.PatternBars == 0) _played[_master] = 0;      // the master track has just wrapped
        ChooseMaster();
    }

    private void BeginFade(Fade f, double seconds)
    {
        _fade = f; _fadeLen = Math.Max(1.0, seconds * _fs); _fadeLeft = _fadeLen;
        Volatile.Write(ref _pubFade, f == Fade.Out ? 1 : 2);
        if (f == Fade.Out) Emit(SeqTapKind.FadeOutStart, -1, -1, 0, 0);
    }

    private void OnFadeEnd()
    {
        if (_fade == Fade.In) { _fade = Fade.None; Volatile.Write(ref _pubFade, 0); return; }
        // fade-out complete: silence -> next palette, fading back in
        Emit(SeqTapKind.FadeOutEnd, -1, -1, 0, 0);
        _fade = Fade.None;
        var ch = _chain;
        if (ch == null) { Volatile.Write(ref _pubFade, 0); return; }
        int next = _chainIdx + 1;
        if (next >= ch.Items.Length)
        {
            if (ch.Loop) next = 0;
            else
            {
                StopTransport(kill: true);
                BeginFade(Fade.In, 2.0);          // soft return of the master level (reverb tail of the fade)
                return;
            }
        }
        _chainIdx = next;
        Volatile.Write(ref _pubChainIdx, next);
        KillVoices();
        SwitchTo(ch.Items[next]);
        BeginFade(Fade.In, ch.FadeInSeconds > 0 ? ch.FadeInSeconds : ch.FadeSeconds);
    }

    private void AdvanceChain()
    {
        var ch = _chain!;
        int next = _chainIdx + 1;
        if (next >= ch.Items.Length)
        {
            if (ch.Loop) next = 0;
            else { StopTransport(kill: false); return; }
        }
        _chainIdx = next;
        Volatile.Write(ref _pubChainIdx, next);
        SwitchTo(ch.Items[next]);
    }

    // ------------------------------------------------------------------ steps

    private uint NextRandom()
    {
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return _rng = x;
    }

    private void FireStep(int t)
    {
        var pat = _active!.Pattern;
        int len = Len(t);
        if (_played[t] >= len) _played[t] = 0;          // wrap (the master's cycle boundary already ran)
        int idx = _played[t];
        if (_p.Get(GlobalParamId.RndSeq) >= 0.5f)
        {
            int ri = (int)MathF.Round(_p.Get(t, ParamId.RndStep));
            int unit = ri switch { 1 => 1, 2 => 2, 3 => 4, 4 => 8, 5 => 16, _ => 0 };
            if (unit > 0)
            {
                unit = Math.Min(unit, len);
                int blocks = Math.Max(1, len / unit);
                int off = _played[t] % unit;
                if (off == 0) _blk[t] = (int)(NextRandom() % (uint)blocks);
                idx = Math.Min(MaxSteps - 1, _blk[t] * unit + off);
            }
        }
        double stepSamples = StepSamples(t);
        _curStep[t] = idx; _absSteps[t]++; _curLen[t] = stepSamples;
        int f = t * MaxSteps + idx;
        int prob = pat.Probability[f];
        bool play = prob >= 100 || (NextRandom() & 0xFFFFFF) * (1.0 / 0x1000000) < prob / 100.0;
        Emit(SeqTapKind.Step, t, idx, play ? 1 : 0, 0);
        if (play)
        {
            ApplyLocks(t, pat, f);
            StartNotes(t, pat, f, stepSamples);
        }
        _played[t]++;
        _toNext[t] += stepSamples;
    }

    private void ApplyLocks(int t, CompiledPattern pat, int f)
    {
        int lc = pat.LockCount[f];
        if (lc == 0 && !_hasOvr[t]) return;
        _p.ClearOverrides(t);
        int ls = pat.LockStart[f];
        for (int i = 0; i < lc; i++) _p.SetOverride(t, pat.Locks[ls + i].Id, pat.Locks[ls + i].Value);
        _hasOvr[t] = lc > 0;
    }

    private void StartNotes(int t, CompiledPattern pat, int f, double stepSamples)
    {
        int nc = pat.NoteCount[f], ns = pat.NoteStart[f];
        for (int i = 0; i < nc; i++)
        {
            var n = pat.Notes[ns + i];
            int note = n.Note + _transpose;
            if ((uint)note > 127) continue;
            // a new note-on of the same pitch takes over from a still-pending one
            var pn = _pendNote[t]; var pl = _pendLeft[t];
            for (int k = 0; k < _pendCount[t]; k++)
                if (pn[k] == note) { int last = --_pendCount[t]; pn[k] = pn[last]; pl[k] = pl[last]; break; }
            Emit(SeqTapKind.NoteOn, t, _curStep[t], note, n.Velocity);
            _host.SeqNoteOn(t, note, n.Velocity);
            if (_pendCount[t] < PendCap)
            {
                int c = _pendCount[t]++;
                pn[c] = (byte)note;
                pl[c] = Math.Max(1.0, (n.Tie - 1 + DefaultGate) * stepSamples);
            }
            else _host.SeqNoteOff(t, note);     // pending list full (cannot happen with 64 steps x 8 notes): do not leave it hanging
        }
    }

    // ------------------------------------------------------------------ publishing

    private void Publish()
    {
        Volatile.Write(ref _pubState, (int)_state);
        Volatile.Write(ref _pubSample, _abs);
        for (int t = 0; t < Tracks; t++)
        {
            Volatile.Write(ref _pubStep[t], _running[t] ? _curStep[t] : -1);
            double phase = _curLen[t] > 0 ? 1.0 - Math.Clamp(_toNext[t] / _curLen[t], 0.0, 1.0) : 0.0;
            Volatile.Write(ref _pubAbsBits[t], BitConverter.DoubleToInt64Bits(_absSteps[t] - 1 + phase));
        }
        if (_state == TransportState.Playing) Volatile.Write(ref _pubPre, 0);
        float g = 1f;
        if (_fade != Fade.None)
        {
            double prog = 1.0 - Math.Clamp(_fadeLeft / _fadeLen, 0.0, 1.0);
            g = _fade == Fade.Out ? (float)Math.Cos(prog * Math.PI * 0.5) : (float)Math.Sin(prog * Math.PI * 0.5);
        }
        Volatile.Write(ref _pubFadeGainBits, BitConverter.SingleToInt32Bits(g));
        if (_fade == Fade.None) Volatile.Write(ref _pubFade, 0);
    }

    private void Emit(SeqTapKind kind, int track, int step, int note, int vel)
    {
        var tap = Tap;
        if (tap != null) tap(new SeqTapEvent(_abs, kind, track, step, note, vel));
    }
}
