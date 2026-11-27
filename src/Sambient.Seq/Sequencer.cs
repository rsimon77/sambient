using Sambient.Engine;

namespace Sambient.Seq;

public enum BpmMode { Pattern, Global }
public enum MuteMode { Sound = 0, Sequence = 1 }

public sealed record StepSnapshot(IReadOnlyList<StepNoteData> Notes, int Probability, IReadOnlyList<ParamLock> Locks)
{
    public bool HasNotes => Notes.Count > 0;
    public bool HasLocks => Locks.Count > 0;
}

public sealed record TrackSnapshot(int Track, int Length, StepNote StepNote, int RndStep, IReadOnlyList<StepSnapshot> Steps);

public sealed record ChainStatus(bool Active, int Index, int Count, int CycleCount, bool Relaxation, int FadePhase, float FadeGain);

/// <summary>
/// The sequencer as the UI / MIDI side sees it: model edits, transport, recording, palette / bank / chain / relaxation.
/// Wraps the audio-thread <c>SequencerCore</c> (attached to the engine in the constructor).
///
/// THREADING. Edits run on any non-audio thread (they take one short lock; call <see cref="Poll"/> from the UI timer too). The
/// authoritative sequence data is the mutable <see cref="PatternPalette"/> model; after every edit an immutable
/// <see cref="CompiledPattern"/> is built (allocation on the UI side only) and published to the audio thread by one volatile
/// reference swap, so the audio thread never sees a half-edited step and never waits. Transport, palette loads, chains and
/// live parameter overrides travel to the audio thread through a lock-free command queue drained at the start of each
/// <c>Render</c>. Position, state and "active palette" travel back as volatile fields. All bank / slot / track / step arguments are 0-BASED.
///
/// BASE vs EFFECTIVE parameters. Knobs and screens use <c>engine.Parameters.GetBase</c> (P-locks never show there);
/// the layers read <c>Get</c> (effective = base unless the playing step locks it).
///
/// Recording writes into the model on the calling thread, positions are quantized from the published playhead (block resolution).
/// Notes are recorded transposed back by the current TRANSPOSE so they sound as played. In OVERWRITE mode <see cref="Poll"/> clears the
/// notes of the steps the playhead passes while recording.
/// </summary>
public sealed class Sequencer : INoteListener
{
    private readonly AudioEngine _engine;
    private readonly ParameterStore _p;
    private readonly SequencerCore _core;
    private readonly object _gate = new();

    private PreparedPalette _editing;
    private PreparedPalette? _lastPublished, _pendingImmediate, _queued;
    private StepData? _stepClip;
    private TrackData? _trackClip;
    private readonly int[] _heldStep = [-1, -1, -1, -1];
    private readonly int[] _cursor = new int[4];
    private readonly Dictionary<(int layer, int note), (long abs, int step)> _rtOn = new();
    private readonly long[] _owLast = [long.MinValue, long.MinValue, long.MinValue, long.MinValue];
    private readonly HashSet<long>[] _recordedAbs = [new(), new(), new(), new()];
    private ChainPlan? _plan;
    private int _preCount = 2;

    public PatternBank Bank { get; }
    public AudioEngine Engine => _engine;
    /// <summary>Raised from <see cref="Poll"/> when the audio thread switched to another palette (queued select, chain, relaxation).</summary>
    public event Action? PaletteChanged;

    public Sequencer(AudioEngine engine, PatternBank? bank = null)
    {
        _engine = engine; _p = engine.Parameters;
        Bank = bank ?? new PatternBank();
        _core = new SequencerCore();
        var init = new PatternPalette("INIT");
        init.CaptureParams(_p);
        _editing = new PreparedPalette(init, -1);
        _core.Post(new SeqCommand { Type = CmdType.Adopt, Palette = _editing });
        engine.AttachSequencer(_core);
    }

    /// <summary>Test / diagnostic tap on the audio thread (note events, steps, cycle boundaries, palette switches, fades).</summary>
    public Action<SeqTapEvent>? Tap { get => _core.Tap; set => _core.Tap = value; }

    // ================================================================= settings

    public bool MetronomeOn { get => _core.MetronomeOn; set => _core.MetronomeOn = value; }
    /// <summary>Metronome volume 0..15.</summary>
    public int MetronomeVolume { get => _core.MetronomeVolume; set => _core.MetronomeVolume = value; }
    /// <summary>Pre-count before realtime recording starts: 0 = OFF, 1..8 beats.</summary>
    public int PreCountBeats { get => _preCount; set => _preCount = Math.Clamp(value, 0, 8); }
    /// <summary>Realtime recording armed (REC): while playing, notes of the layer quantize into its track.</summary>
    public bool RecordArmed { get; set; }
    /// <summary>PRM REC: parameter changes through <see cref="SetLayerParam"/> while playing write P-locks to the current step.</summary>
    public bool ParamRecordArmed { get; set; }
    /// <summary>Overwrite: steps passed while recording lose their old notes.</summary>
    public bool OverwriteMode { get; set; }
    /// <summary>PTN: loading a palette loads its BPM; GLBL: the current BPM is kept.</summary>
    public BpmMode BpmMode { get => _core.BpmFromPalette ? BpmMode.Pattern : BpmMode.Global; set => _core.BpmFromPalette = value == BpmMode.Pattern; }
    /// <summary>R.RST: ON resets the reverb on every palette change; OFF only when the reverb type changes.</summary>
    public bool ResetReverbOnChange { get => _core.ResetReverbOnChange; set => _core.ResetReverbOnChange = value; }
    /// <summary>SND: a muted layer is silent; SEQ: only its sequencer notes are muted (the keyboard still plays it).</summary>
    public MuteMode MuteMode { get => (MuteMode)(int)_p.Get(GlobalParamId.MuteMode); set => _engine.SetGlobal(GlobalParamId.MuteMode, (float)value); }
    /// <summary>Seeds the probability / RND SEQ random generator (applied in command order on the audio thread).</summary>
    public void Seed(uint seed) => _core.Post(new SeqCommand { Type = CmdType.Seed, A = (int)seed });

    /// <summary>Explicit pattern length in 4/4 bars (1..512) for chain switching; 0 = the longest track cycle.</summary>
    public int PatternBars
    {
        get => _editing.Model.PatternBars;
        set { lock (_gate) { _editing.Model.PatternBars = Math.Clamp(value, 0, 512); _editing.Republish(); } }
    }

    // ================================================================= transport + playhead

    public TransportState State => _core.State;
    public bool IsPlaying => State != TransportState.Stopped;
    /// <summary>Beats of pre-count still to come (during <see cref="TransportState.PreCount"/>).</summary>
    public int PreCountLeft => _core.PreCountLeft;
    /// <summary>Completed pattern cycles of the active palette since it started.</summary>
    public int CycleCount => _core.CycleCount;
    /// <summary>Current step of a track (-1 when stopped).</summary>
    public int CurrentStep(int track) => _core.CurrentStep(track);
    /// <summary>Position inside the current step 0..1.</summary>
    public double StepPhase(int track) { double a = _core.AbsPosition(track); return a - Math.Floor(a); }
    public long SamplePosition => _core.SamplePosition;
    public long DroppedCommands => _core.DroppedCommands;

    /// <summary>Play from step 1. With <see cref="RecordArmed"/> and a pre-count, the tracks start after the counted beats.</summary>
    public void Play() => _core.Post(new SeqCommand { Type = CmdType.Play, A = RecordArmed ? _preCount : 0 });

    /// <summary>Normal STOP: stops the sequence and releases every voice at once (short click-free release).</summary>
    public void Stop() { FinishRecording(); _core.Post(new SeqCommand { Type = CmdType.Stop }); }

    /// <summary>Stop with natural release: the sequence stops, sounding notes keep their release tails.</summary>
    public void StopNatural() { FinishRecording(); _core.Post(new SeqCommand { Type = CmdType.StopNatural }); }

    private void FinishRecording()
    {
        lock (_gate) { _rtOn.Clear(); Array.Fill(_owLast, long.MinValue); foreach (var h in _recordedAbs) h.Clear(); }
    }

    // ================================================================= model access

    private static void CheckTrack(int t) { if ((uint)t >= SeqLimits.Tracks) throw new ArgumentOutOfRangeException(nameof(t)); }
    private static void CheckStep(int s) { if ((uint)s >= SeqLimits.MaxSteps) throw new ArgumentOutOfRangeException(nameof(s)); }

    private int Length(int track) => Math.Clamp((int)MathF.Round(_p.GetBase(track, ParamId.SeqLength)), 1, SeqLimits.MaxSteps);

    private void Mutate(Action<PatternPalette> edit)
    {
        lock (_gate) { edit(_editing.Model); _editing.Republish(); }
    }

    private static StepSnapshot Snap(StepData s) => new(s.Notes.ToArray(), s.Probability, s.Locks.Select(k => new ParamLock(k.Key, k.Value)).ToArray());

    public StepSnapshot GetStep(int track, int step)
    {
        CheckTrack(track); CheckStep(step);
        lock (_gate) return Snap(_editing.Model.Tracks[track].Steps[step]);
    }

    public TrackSnapshot GetTrack(int track)
    {
        CheckTrack(track);
        lock (_gate)
        {
            var steps = _editing.Model.Tracks[track].Steps.Select(Snap).ToArray();
            return new TrackSnapshot(track, Length(track), (StepNote)(int)_p.GetBase(track, ParamId.StepNote),
                (int)_p.GetBase(track, ParamId.RndStep), steps);
        }
    }

    // ---- track settings (layer parameters)
    public void SetTrackLength(int track, int length) { CheckTrack(track); _engine.SetParam(track, ParamId.SeqLength, length); }
    public void SetTrackStepNote(int track, StepNote note) { CheckTrack(track); _engine.SetParam(track, ParamId.StepNote, (float)note); }
    /// <summary>RND STEP unit index: 0 = OFF, 1 = 1, 2 = 2, 3 = 4, 4 = 8, 5 = 16 steps.</summary>
    public void SetTrackRndStep(int track, int index) { CheckTrack(track); _engine.SetParam(track, ParamId.RndStep, index); }
    public void SetRndSeq(bool on) => _engine.SetGlobal(GlobalParamId.RndSeq, on ? 1f : 0f);
    public void SetTranspose(int semitones) => _engine.SetGlobal(GlobalParamId.Transpose, semitones);

    // ================================================================= step editing

    /// <summary>Toggles a note on a step (step recording). Returns true when the note is now on, false when removed or the step is full.</summary>
    public bool SetStepNote(int track, int step, int note, int velocity = 100)
    {
        CheckTrack(track); CheckStep(step);
        bool on = false;
        Mutate(m =>
        {
            var notes = m.Tracks[track].Steps[step].Notes;
            int i = notes.FindIndex(n => n.Note == note);
            if (i >= 0) { notes.RemoveAt(i); return; }
            if (notes.Count >= SeqLimits.MaxNotesPerStep) return;
            notes.Add(StepNoteData.Make(note, velocity)); on = true;
        });
        return on;
    }

    /// <summary>Adds (or re-velocities) a note without toggling. Returns false when the step already holds 8 other notes.</summary>
    public bool AddStepNote(int track, int step, int note, int velocity = 100, int tie = 1)
    {
        CheckTrack(track); CheckStep(step);
        bool ok = true;
        Mutate(m => ok = AddNote(m.Tracks[track].Steps[step], StepNoteData.Make(note, velocity, ClampTie(track, step, tie))));
        return ok;
    }

    private static bool AddNote(StepData st, StepNoteData n)
    {
        int i = st.Notes.FindIndex(x => x.Note == n.Note);
        if (i >= 0) { st.Notes[i] = n; return true; }
        if (st.Notes.Count >= SeqLimits.MaxNotesPerStep) return false;
        st.Notes.Add(n);
        return true;
    }

    private int ClampTie(int track, int step, int tie) => Math.Clamp(tie, 1, Math.Max(1, Length(track) - step));

    /// <summary>Removes all notes, locks and the probability of a step.</summary>
    public void ClearStep(int track, int step) { CheckTrack(track); CheckStep(step); Mutate(m => m.Tracks[track].Steps[step].Clear()); }

    public void CopyStep(int track, int step)
    {
        CheckTrack(track); CheckStep(step);
        lock (_gate) _stepClip = _editing.Model.Tracks[track].Steps[step].Clone();
    }

    /// <summary>Pastes the copied step (notes, probability, locks). False when nothing was copied.</summary>
    public bool PasteStep(int track, int step)
    {
        CheckTrack(track); CheckStep(step);
        lock (_gate)
        {
            if (_stepClip == null) return false;
            _editing.Model.Tracks[track].Steps[step] = _stepClip.Clone();
            _editing.Republish();
            return true;
        }
    }

    /// <summary>
    /// Tie input: a note sounding from <paramref name="startStep"/> through <paramref name="endStep"/> (inclusive). Ties cannot wrap
    /// from the end of the track to its start; returns false when end &lt; start or the step is full.
    /// </summary>
    public bool SetTie(int track, int startStep, int endStep, int note, int velocity = 100)
    {
        CheckTrack(track); CheckStep(startStep); CheckStep(endStep);
        if (endStep < startStep) return false;
        bool ok = true;
        Mutate(m => ok = AddNote(m.Tracks[track].Steps[startStep], StepNoteData.Make(note, velocity, ClampTie(track, startStep, endStep - startStep + 1))));
        return ok;
    }

    // ---- note-level editing (piano roll): precise operations, no toggling. Rules and conflict behaviour: see <see cref="NoteEditing"/>.

    /// <summary>Removes one note of a step. False when the step has no such note.</summary>
    public bool RemoveStepNote(int track, int step, int note)
        => ApplyNoteEdits(track, [NoteEdit.Remove(step, note)]).Succeeded[0];

    /// <summary>
    /// Moves a note to another step and / or pitch, keeping velocity and tie (the tie is clamped so it does not pass the track end).
    /// False - and nothing changes - when the note does not exist, the target is out of range, the target step already holds 8 notes
    /// or the target already holds that pitch. Moving a note onto its own place succeeds.
    /// </summary>
    public bool MoveNote(int track, int fromStep, int note, int toStep, int toNote)
        => ApplyNoteEdits(track, [NoteEdit.Move(fromStep, note, toStep, toNote)]).Succeeded[0];

    /// <summary>Sets the tie length of one note (clamped to 1 .. steps left in the track). False when the note does not exist.</summary>
    public bool SetNoteTie(int track, int step, int note, int tie)
        => ApplyNoteEdits(track, [NoteEdit.SetTie(step, note, tie)]).Succeeded[0];

    /// <summary>Sets the velocity (1..127) of one note. False when the note does not exist.</summary>
    public bool SetNoteVelocity(int track, int step, int note, int velocity)
        => ApplyNoteEdits(track, [NoteEdit.SetVelocity(step, note, velocity)]).Succeeded[0];

    /// <summary>
    /// Applies a group of note edits (moves, removes, adds, tie / velocity changes) atomically: one lock, one compile, one publish, so
    /// the audio thread never sees a half-moved selection. Order and conflict rules: see <see cref="NoteEditing"/>.
    /// </summary>
    public NoteEditResult ApplyNoteEdits(int track, IReadOnlyList<NoteEdit> edits)
    {
        CheckTrack(track);
        NoteEditResult? result = null;
        Mutate(m => result = NoteEditing.Apply(m.Tracks[track], Length(track), edits));
        return result!;
    }

    /// <summary>A deep copy of a track's steps (notes, probabilities, locks) for undo.</summary>
    public TrackData SnapshotTrack(int track)
    {
        CheckTrack(track);
        lock (_gate) return _editing.Model.Tracks[track].Clone();
    }

    /// <summary>Replaces a track's steps by (a copy of) a snapshot taken with <see cref="SnapshotTrack"/>; published atomically.</summary>
    public void RestoreTrack(int track, TrackData snapshot)
    {
        CheckTrack(track);
        Mutate(m => m.Tracks[track].CopyFrom(snapshot));
    }

    /// <summary>Step probability 25..100 % (the DICE setting of the step).</summary>
    public void SetStepProbability(int track, int step, int percent)
    {
        CheckTrack(track); CheckStep(step);
        Mutate(m => m.Tracks[track].Steps[step].Probability = Math.Clamp(percent, SeqLimits.MinProbability, 100));
    }

    // ---- p-locks
    /// <summary>Locks a layer parameter on a step (direct p-lock). False when the parameter is not lockable.</summary>
    public bool SetLock(int track, int step, ParamId id, float value)
    {
        CheckTrack(track); CheckStep(step);
        if (!LockRules.IsLockable(id)) return false;
        float v = ParamTable.Clamp(id, value);
        Mutate(m => m.Tracks[track].Steps[step].Locks[id] = v);
        if (id == ParamId.Wave) RequestWave(track, (int)v);
        return true;
    }

    public void ClearLock(int track, int step, ParamId id) { CheckTrack(track); CheckStep(step); Mutate(m => m.Tracks[track].Steps[step].Locks.Remove(id)); }

    /// <summary>Removes the locks of one step.</summary>
    public void ClearLocks(int track, int step) { CheckTrack(track); CheckStep(step); Mutate(m => m.Tracks[track].Steps[step].Locks.Clear()); }

    /// <summary>Removes the locks of every step of a track.</summary>
    public void ClearLocks(int track) { CheckTrack(track); Mutate(m => { foreach (var s in m.Tracks[track].Steps) s.Locks.Clear(); }); }

    /// <summary>Sound lock: snapshots WAVE, HARMONIC, Balance, PITCH, Detune, MOD rate and MOD depth (base values) into the step.</summary>
    public void SetSoundLock(int track, int step)
    {
        CheckTrack(track); CheckStep(step);
        Mutate(m => { foreach (var id in LockRules.SoundLock) m.Tracks[track].Steps[step].Locks[id] = _p.GetBase(track, id); });
    }

    /// <summary>
    /// Parameter change from a knob / screen. While PRM REC is armed and the sequencer plays, a lockable parameter is written as a
    /// P-lock to the current step (and sounds at once as a temporary override) instead of changing the base value; otherwise it
    /// sets the base value (and requests wave builds as <see cref="AudioEngine.SetParam"/> does).
    /// </summary>
    public float SetLayerParam(int layer, ParamId id, float value)
    {
        if (ParamRecordArmed && State == TransportState.Playing && LockRules.IsLockable(id))
        {
            int step = _core.CurrentStep(layer);
            if (step >= 0)
            {
                float v = ParamTable.Clamp(id, value);
                SetLock(layer, step, id, v);
                _core.Post(new SeqCommand { Type = CmdType.LiveParam, A = layer, B = (int)id, F = v });
                return v;
            }
        }
        return _engine.SetParam(layer, id, value);
    }

    private void RequestWave(int layer, int wave)
    {
        if (layer == ParameterStore.NoiseLayerIndex) _engine.Noise.EnsureBuiltAsync(wave);
        else _engine.Bank.EnsureBuiltAsync(wave);
    }

    // ---- tracks
    /// <summary>Clears all notes of a track (locks and probabilities stay).</summary>
    public void ClearTrackNotes(int track) { CheckTrack(track); Mutate(m => { foreach (var s in m.Tracks[track].Steps) s.Notes.Clear(); }); }

    /// <summary>Clears notes, locks and probabilities of a track.</summary>
    public void ClearTrack(int track) { CheckTrack(track); Mutate(m => { foreach (var s in m.Tracks[track].Steps) s.Clear(); }); }

    public void CopyTrack(int track) { CheckTrack(track); lock (_gate) _trackClip = _editing.Model.Tracks[track].Clone(); }

    public bool PasteTrack(int track)
    {
        CheckTrack(track);
        lock (_gate)
        {
            if (_trackClip == null) return false;
            _editing.Model.Tracks[track].CopyFrom(_trackClip);
            _editing.Republish();
            return true;
        }
    }

    /// <summary>
    /// Extends the track by copying its steps: length x2 or x4 (up to 64). Returns the new length.
    /// </summary>
    public int DuplicateTrack(int track, int factor)
    {
        CheckTrack(track);
        if (factor is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(factor), "factor must be 2 or 4");
        int len = Length(track), newLen = Math.Min(SeqLimits.MaxSteps, len * factor);
        Mutate(m =>
        {
            var steps = m.Tracks[track].Steps;
            for (int i = len; i < newLen; i++) steps[i] = steps[i % len].Clone();
        });
        _engine.SetParam(track, ParamId.SeqLength, newLen);
        return newLen;
    }

    // ---- auto step
    public int StepCursor(int track) { CheckTrack(track); return _cursor[track]; }
    public void SetStepCursor(int track, int step) { CheckTrack(track); _cursor[track] = Math.Clamp(step, 0, Length(track) - 1); }

    /// <summary>Auto-step input: puts the notes (a chord) on the cursor step and moves the cursor on by one step (wrapping at the track length). Returns the step used.</summary>
    public int StepInput(int track, IEnumerable<int> notes, int velocity = 100, int tie = 1)
    {
        CheckTrack(track);
        int step = _cursor[track];
        foreach (var n in notes) AddStepNote(track, step, n, velocity, tie);
        _cursor[track] = (step + 1) % Length(track);
        return step;
    }

    /// <summary>Auto-step REST: moves the cursor on without writing.</summary>
    public void StepRest(int track) { CheckTrack(track); _cursor[track] = (_cursor[track] + 1) % Length(track); }

    // ================================================================= recording

    /// <summary>Direct recording: while a step is held, incoming notes of that track go onto it. Release with <see cref="ReleaseStep"/>.</summary>
    public void HoldStep(int track, int step) { CheckTrack(track); CheckStep(step); lock (_gate) _heldStep[track] = step; }
    public void ReleaseStep(int track) { CheckTrack(track); lock (_gate) _heldStep[track] = -1; }

    void INoteListener.NoteOn(int layer, int note, int velocity) => RecordNoteOn(layer, note, velocity);
    void INoteListener.NoteOff(int layer, int note) => RecordNoteOff(layer, note);

    /// <summary>Feeds a played note to the recorders (direct, then realtime). Called by the note router.</summary>
    public void RecordNoteOn(int layer, int note, int velocity)
    {
        if ((uint)layer >= SeqLimits.Tracks) return;
        if (velocity <= 0) velocity = (int)MathF.Round(_p.GetBase(layer, ParamId.Velocity));
        lock (_gate)
        {
            int held = _heldStep[layer];
            if (held >= 0)
            {
                AddNote(_editing.Model.Tracks[layer].Steps[held], StepNoteData.Make(note, velocity));
                _editing.Republish();
                return;
            }
            if (!RecordArmed || State != TransportState.Playing) return;
            int cur = _core.CurrentStep(layer);
            if (cur < 0) return;
            double pos = _core.AbsPosition(layer);
            long fl = (long)Math.Floor(pos), q = (long)Math.Round(pos, MidpointRounding.AwayFromZero);
            int len = Length(layer);
            int step = (int)(((cur + (q - fl)) % len + len) % len);
            int stored = Math.Clamp(note - (int)MathF.Round(_p.Get(GlobalParamId.Transpose)), 0, 127);
            AddNote(_editing.Model.Tracks[layer].Steps[step], StepNoteData.Make(stored, velocity));
            _rtOn[(layer, note)] = (q, step);
            if (OverwriteMode) _recordedAbs[layer].Add(q);
            _editing.Republish();
        }
    }

    /// <summary>Ends a realtime-recorded note: its duration becomes the tie length (in steps, at least 1, no wrap).</summary>
    public void RecordNoteOff(int layer, int note)
    {
        if ((uint)layer >= SeqLimits.Tracks) return;
        lock (_gate)
        {
            if (!_rtOn.Remove((layer, note), out var on)) return;
            double pos = _core.AbsPosition(layer);
            long q = (long)Math.Round(pos, MidpointRounding.AwayFromZero);
            int tie = ClampTie(layer, on.step, (int)Math.Clamp(q - on.abs, 1, SeqLimits.MaxSteps));
            int stored = Math.Clamp(note - (int)MathF.Round(_p.Get(GlobalParamId.Transpose)), 0, 127);
            var steps = _editing.Model.Tracks[layer].Steps;
            var st = steps[on.step];
            int i = st.Notes.FindIndex(n => n.Note == stored);
            if (i >= 0) { st.Notes[i] = st.Notes[i] with { Tie = tie }; _editing.Republish(); }
        }
    }

    // ================================================================= UI pump

    /// <summary>
    /// Call from the UI timer (30..60 Hz) and after offline renders in tests: adopts a palette the audio thread switched to
    /// (raising <see cref="PaletteChanged"/>) and performs the overwrite clearing of recording.
    /// </summary>
    public void Poll()
    {
        bool changed = false;
        lock (_gate)
        {
            var pub = _core.ActivePalette;
            if (pub != null && !ReferenceEquals(pub, _lastPublished))
            {
                _lastPublished = pub;
                if (_pendingImmediate == null || ReferenceEquals(pub, _pendingImmediate))
                {
                    if (!ReferenceEquals(_editing, pub)) { _editing = pub; changed = true; }
                    _pendingImmediate = null;
                }
                if (ReferenceEquals(pub, _queued)) { _queued = null; changed = true; }
            }
            if (OverwriteMode && RecordArmed && State == TransportState.Playing) OverwritePass();
        }
        if (changed) PaletteChanged?.Invoke();
    }

    private void OverwritePass()
    {
        for (int t = 0; t < SeqLimits.Tracks; t++)
        {
            int cur = _core.CurrentStep(t);
            if (cur < 0) continue;
            long now = (long)Math.Floor(_core.AbsPosition(t));
            if (_owLast[t] == long.MinValue) { _owLast[t] = now; continue; }
            if (now <= _owLast[t]) continue;
            int len = Length(t);
            long from = Math.Max(_owLast[t] + 1, now - len + 1);
            bool any = false;
            for (long a = from; a <= now; a++)
            {
                if (_recordedAbs[t].Contains(a)) continue;
                int idx = (int)(((cur - (now - a)) % len + len) % len);
                var st = _editing.Model.Tracks[t].Steps[idx];
                if (st.Notes.Count > 0) { st.Notes.Clear(); any = true; }
            }
            _recordedAbs[t].RemoveWhere(a => a <= now);
            _owLast[t] = now;
            if (any) _editing.Republish();
        }
    }

    // ================================================================= palettes

    private PreparedPalette Prepare(PatternPalette model, int slot)
    {
        var p = new PreparedPalette(model, slot);
        int n = ParamTable.LayerParamCount;
        for (int l = 0; l < SeqLimits.Tracks; l++)
        {
            RequestWave(l, (int)p.LayerBase[l * n + (int)ParamId.Wave]);
            foreach (var s in model.Tracks[l].Steps)
                if (s.Locks.TryGetValue(ParamId.Wave, out var w)) RequestWave(l, (int)w);
        }
        return p;
    }

    private PreparedPalette PrepareSlot(int bank, int slot) => Prepare(Bank.Load(bank, slot), PatternBank.SlotIndex(bank, slot));

    /// <summary>Bank (0..7) of the palette being edited, or -1 for an unsaved / INIT palette.</summary>
    public int CurrentBank => _editing.Slot < 0 ? -1 : _editing.Slot / PatternBank.SlotsPerBank;
    public int CurrentSlot => _editing.Slot < 0 ? -1 : _editing.Slot % PatternBank.SlotsPerBank;
    public string CurrentName => _editing.Model.Name;
    /// <summary>The (bank, slot) waiting for the end of the pattern cycle, if a select is queued.</summary>
    public (int Bank, int Slot)? QueuedSlot { get { var q = _queued; return q == null || q.Slot < 0 ? null : (q.Slot / PatternBank.SlotsPerBank, q.Slot % PatternBank.SlotsPerBank); } }

    /// <summary>
    /// Selects a palette. Stopped: it loads immediately. Playing: it is queued and switches when the current pattern cycle completes.
    /// </summary>
    public void SelectPalette(int bank, int slot)
    {
        var prep = PrepareSlot(bank, slot);
        if (State == TransportState.Stopped) LoadPrepared(prep);
        else
        {
            lock (_gate) _queued = prep;
            _core.Post(new SeqCommand { Type = CmdType.Queue, Palette = prep });
        }
    }

    /// <summary>Loads a palette at once, restarting the sequence if it plays.</summary>
    public void LoadPaletteNow(int bank, int slot) => LoadPrepared(PrepareSlot(bank, slot));

    /// <summary>
    /// Loads a standalone palette model (e.g. a <see cref="Capture"/> carried over to a rebuilt sequencer) as the edited palette.
    /// <paramref name="slot"/> is the bank slot index (bank * 16 + slot) it should be remembered as, or -1 for an unsaved palette.
    /// </summary>
    public void LoadModel(PatternPalette model, int slot = -1) => LoadPrepared(Prepare(model, slot));

    /// <summary>True when the audio thread has made the edited palette the active one (its parameters are in the store).</summary>
    public bool PaletteApplied { get { lock (_gate) return ReferenceEquals(_core.ActivePalette, _editing); } }

    private void LoadPrepared(PreparedPalette prep)
    {
        lock (_gate) { _editing = prep; _pendingImmediate = prep; _queued = null; _plan = null; }
        _core.Post(new SeqCommand { Type = CmdType.Load, Palette = prep });
    }

    /// <summary>Restores the saved state of the current slot (factory preset or user file); INIT for an unsaved palette.</summary>
    public void Reload()
    {
        int slot = _editing.Slot;
        if (slot < 0) Init(); else LoadPaletteNow(slot / PatternBank.SlotsPerBank, slot % PatternBank.SlotsPerBank);
    }

    /// <summary>The palette being edited as a standalone copy: base parameter values from the store + the sequences.</summary>
    public PatternPalette Capture()
    {
        lock (_gate)
        {
            var src = _editing.Model;
            var c = new PatternPalette(src.Name) { PatternBars = src.PatternBars };
            c.CaptureParams(_p);
            for (int t = 0; t < SeqLimits.Tracks; t++) c.Tracks[t] = src.Tracks[t].Clone();
            return c;
        }
    }

    /// <summary>Saves into the current slot. False for an unsaved palette (use <see cref="SaveAs"/>).</summary>
    public bool Save()
    {
        int slot = _editing.Slot;
        if (slot < 0) return false;
        SaveAs(slot / PatternBank.SlotsPerBank, slot % PatternBank.SlotsPerBank);
        return true;
    }

    /// <summary>Saves into another slot; the edited palette now lives there.</summary>
    public void SaveAs(int bank, int slot)
    {
        var c = Capture();
        Bank.Save(bank, slot, c);
        lock (_gate) { _editing.Slot = PatternBank.SlotIndex(bank, slot); }   // keep editing the same sequence model; only its slot identity changes
    }

    /// <summary>INIT: clears the sound and the sequences (loaded at once).</summary>
    public void Init() => LoadPrepared(Prepare(new PatternPalette("INIT"), -1));

    /// <summary>Renames the edited palette (4..8 characters); returns the normalized name.</summary>
    public string Rename(string name)
    {
        lock (_gate) { _editing.Model.Name = name; return _editing.Model.Name; }
    }

    /// <summary>
    /// Copies sound + sequence of one layer onto another layer of the same type (layers 1..3 with each other, layer 4 only onto itself)
    /// within the edited palette. False if the types differ.
    /// </summary>
    public bool CopyLayer(int from, int to)
    {
        if (!PatternPalette.CanCopyLayer(from, to)) return false;
        float[] vals;
        lock (_gate)
        {
            vals = new float[ParamTable.LayerParamCount];
            foreach (var id in Enum.GetValues<ParamId>()) vals[(int)id] = _p.GetBase(from, id);
            _editing.Model.Tracks[to] = _editing.Model.Tracks[from].Clone();
            _editing.Republish();
        }
        ApplyLayerValues(to, vals);
        return true;
    }

    /// <summary>Copies sound + sequence of a layer of another palette (a bank slot) onto a layer of the edited palette (same layer type only).</summary>
    public bool CopyLayerFrom(int bank, int slot, int fromLayer, int toLayer)
    {
        if (!PatternPalette.CanCopyLayer(fromLayer, toLayer)) return false;
        var src = Bank.Load(bank, slot);
        lock (_gate)
        {
            _editing.Model.Tracks[toLayer] = src.Tracks[fromLayer].Clone();
            _editing.Republish();
        }
        ApplyLayerValues(toLayer, src.Layers[fromLayer]);
        foreach (var s in src.Tracks[fromLayer].Steps)
            if (s.Locks.TryGetValue(ParamId.Wave, out var w)) RequestWave(toLayer, (int)w);
        return true;
    }

    private void ApplyLayerValues(int layer, float[] values)
    {
        foreach (var id in Enum.GetValues<ParamId>())
            if (id != ParamId.Mute) _p.Set(layer, id, values[(int)id]);      // direct store write: palette values are final (no DRONE octave side effect)
        RequestWave(layer, (int)_p.GetBase(layer, ParamId.Wave));
        lock (_gate)
            foreach (var s in _editing.Model.Tracks[layer].Steps)
                if (s.Locks.TryGetValue(ParamId.Wave, out var w)) RequestWave(layer, (int)w);
    }

    // ================================================================= chain + relaxation

    /// <summary>Builds a chain from bank slots. Repeats: pattern cycles per palette (Relaxation: clamped to 2..10); fade seconds snap to 5..30 in steps of 5.</summary>
    public void SetChain(IReadOnlyList<(int Bank, int Slot)> slots, bool loop = false, int repeats = 1, bool relaxation = false,
        int fadeSeconds = 10, int fadeInSeconds = 0)
    {
        if (slots.Count == 0) { ClearChain(); return; }
        var items = new PreparedPalette[slots.Count];
        for (int i = 0; i < items.Length; i++) items[i] = PrepareSlot(slots[i].Bank, slots[i].Slot);
        var plan = new ChainPlan
        {
            Items = items, Loop = loop,
            Repeats = relaxation ? Math.Clamp(repeats, 2, 10) : Math.Clamp(repeats, 1, 99),
            Relaxation = relaxation,
            FadeSeconds = Math.Clamp((int)Math.Round(fadeSeconds / 5.0) * 5, 5, 30),
            FadeInSeconds = fadeInSeconds,
        };
        lock (_gate) _plan = plan;
    }

    public void ClearChain()
    {
        lock (_gate) _plan = null;
        _core.Post(new SeqCommand { Type = CmdType.ClearChain });
    }

    /// <summary>Starts the chain from its first palette (loaded at once), playing from step 1.</summary>
    public bool PlayChain() => PlayChainFrom(0);

    /// <summary>Starts the chain at entry <paramref name="startIndex"/> (clamped; loaded at once), playing from step 1; the chain continues from there.</summary>
    public bool PlayChainFrom(int startIndex)
    {
        ChainPlan? plan;
        lock (_gate) plan = _plan;
        if (plan == null) return false;
        int i = Math.Clamp(startIndex, 0, plan.Items.Length - 1);
        lock (_gate) { _editing = plan.Items[i]; _pendingImmediate = plan.Items[i]; _queued = null; }
        _core.Post(new SeqCommand { Type = CmdType.Load, Palette = plan.Items[i] });
        _core.Post(new SeqCommand { Type = CmdType.SetChain, Chain = plan, A = i });
        _core.Post(new SeqCommand { Type = CmdType.Play, A = 0 });
        return true;
    }

    public ChainStatus Chain
    {
        get
        {
            ChainPlan? plan; lock (_gate) plan = _plan;
            return new ChainStatus(plan != null, _core.ChainIndex, plan?.Items.Length ?? 0, _core.CycleCount, plan?.Relaxation ?? false,
                _core.FadePhase, _core.FadeGain);
        }
    }
}
