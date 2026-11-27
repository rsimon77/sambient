using Sambient.Dsp.Params;

namespace Sambient.Engine;

/// <summary>What a layer must offer so the <see cref="VoiceModeController"/> can drive its voices.</summary>
public interface IVoiceSink
{
    /// <summary>POLY / UNISON note-on (the sink checks <see cref="VoiceModeController.Mode"/> to pick the allocation).</summary>
    void PolyOn(int note, int velocity);
    /// <summary>POLY / UNISON note-off (subject to the sustain pedal, as before).</summary>
    void PolyOff(int note);
    /// <summary>
    /// Single-voice note: moves the mono voice to <paramref name="note"/> (sliding over <paramref name="glideMs"/> when the
    /// voice is already sounding and glideMs &gt; 0), retriggering its envelope when <paramref name="retrigger"/>.
    /// </summary>
    void MonoStart(int note, int velocity, float glideMs, bool retrigger);
    /// <summary>Releases the mono voice (ignores the sustain pedal: the controller handles it).</summary>
    void MonoStop();
    /// <summary>Releases every voice (gate off, pedal deferrals included).</summary>
    void ReleaseAll();
}

/// <summary>
/// Per-layer note logic for the voice modes (POLY, MONO, LEGATO, UNISON, ARP), HOLD (latch) and the sustain pedal.
/// Audio thread only; no allocations (fixed 32-entry note list).
///
/// State: an ordered list (press order, oldest first) of sounding keys, each with three independent reasons to stay alive:
/// <c>Down</c> (physical key), <c>Latched</c> (HOLD) and <c>Pedal</c> (kept by the sustain pedal; used by the single-voice and
/// ARP modes; POLY/UNISON leave pedal handling to the voices as before). An entry with no reason is removed.
/// MONO / LEGATO play the newest living entry (last-note priority); releasing it returns to the previous one (MONO
/// retriggers, LEGATO slides). ARP feeds all living entries to the <see cref="Arpeggiator"/>.
/// Changing the mode releases everything, then re-feeds the still held (Down / Latched) keys into the new mode.
/// </summary>
public sealed class VoiceModeController
{
    private struct Entry { public int Note, Vel; public bool Down, Latched, Pedal; public bool Alive => Down || Latched || Pedal; }

    private readonly ParameterStore _params;
    private readonly int _layer;
    private readonly IVoiceSink _sink;
    private readonly bool _unison;
    private readonly Entry[] _e = new Entry[Arpeggiator.MaxNotes];
    private readonly int[] _tmpNotes = new int[Arpeggiator.MaxNotes];
    private readonly int[] _tmpVels = new int[Arpeggiator.MaxNotes];
    private int _n;
    private bool _sustain;
    private int _monoNote = -1;
    private VoiceMode _mode = VoiceMode.Poly;

    public Arpeggiator Arp { get; } = new();

    /// <summary>Effective mode (UNISON counts as POLY on layers without unison).</summary>
    public VoiceMode Mode => _mode;

    public VoiceModeController(ParameterStore parameters, int layer, float sampleRate, IVoiceSink sink, bool unisonSupported)
    {
        _params = parameters; _layer = layer; _sink = sink; _unison = unisonSupported;
        Arp.SampleRate = sampleRate;
        _mode = ReadMode();
    }

    private VoiceMode ReadMode()
    {
        var m = (VoiceMode)Math.Clamp((int)MathF.Round(_params.Get(_layer, ParamId.VoiceMode)), 0, 4);
        return m == VoiceMode.Unison && !_unison ? VoiceMode.Poly : m;
    }

    private bool IsPolyLike => _mode is VoiceMode.Poly or VoiceMode.Unison;

    /// <summary>Number of living entries (diagnostic).</summary>
    public int AliveCount { get { int c = 0; for (int i = 0; i < _n; i++) if (_e[i].Alive) c++; return c; } }

    /// <summary>True if <paramref name="note"/> is latched by HOLD.</summary>
    public bool IsLatched(int note) { int i = Find(note); return i >= 0 && _e[i].Latched; }

    /// <summary>Picks up mode / ARP parameter changes. Call at the start of every render block and before every event.</summary>
    public void Sync()
    {
        var m = ReadMode();
        if (m != _mode) ChangeMode(m);
        Arp.Type = (ArpType)Math.Clamp((int)MathF.Round(_params.Get(_layer, ParamId.ArpType)), 0, 11);
        int sn = Math.Clamp((int)MathF.Round(_params.Get(_layer, ParamId.StepNote)), 0, Arpeggiator.StepQuarterNotes.Length - 1);
        Arp.StepQuarters = Arpeggiator.StepQuarterNotes[sn];
        Arp.Bpm = _params.Get(GlobalParamId.Bpm);
    }

    // ------------------------------------------------------------------ events

    public void NoteOn(int note, int velocity)
    {
        Sync();
        Press(note, velocity, down: true);
    }

    public void NoteOff(int note)
    {
        Sync();
        int i = Find(note);
        if (i < 0)
        {
            if (IsPolyLike) _sink.PolyOff(note);     // untracked (list overflow): old behaviour
            return;
        }
        _e[i].Down = false;
        if (!IsPolyLike && _sustain && !_e[i].Latched) _e[i].Pedal = true;
        AfterRelease(note, i);
    }

    /// <summary>HOLD: latches the key (it keeps sounding after release) or, if already latched, releases it.</summary>
    public void HoldToggle(int note, int velocity)
    {
        Sync();
        int i = Find(note);
        if (i >= 0 && _e[i].Latched)
        {
            _e[i].Latched = false;
            AfterRelease(note, i);
            return;
        }
        if (i >= 0) { _e[i].Latched = true; return; }   // key is down already and sounding
        Press(note, velocity, down: false);
        int j = Find(note);
        if (j >= 0) _e[j].Latched = true;
    }

    public void ClearHolds()
    {
        Sync();
        bool any = false;
        for (int i = 0; i < _n; i++) if (_e[i].Latched) { _e[i].Latched = false; any = true; }
        if (any) Reconcile(poly: true);
    }

    /// <summary>Panic: releases every voice and forgets all keys, pedal states and holds.</summary>
    public void AllNotesOff()
    {
        Sync();
        _sink.ReleaseAll();
        _n = 0; _monoNote = -1;
        Arp.Stop();
    }

    public void SetSustain(bool down)
    {
        _sustain = down;
        if (down || IsPolyLike) return;
        bool any = false;
        for (int i = 0; i < _n; i++) if (_e[i].Pedal) { _e[i].Pedal = false; any = true; }
        if (any) Reconcile(poly: false);
    }

    // ------------------------------------------------------------------ arp driving (called by the layer's render loop)

    public bool ArpActive => _mode == VoiceMode.Arp && Arp.Running;

    /// <summary>Fires all arp events that are due now (note-on / gate-off).</summary>
    public void ProcessArpEvents()
    {
        if (!ArpActive) return;
        for (int guard = 0; guard < 8 && Arp.TryNextEvent(out var ev); guard++)
        {
            if (ev.IsNoteOn) _sink.MonoStart(ev.Note, ev.Velocity, 0f, true);
            else _sink.MonoStop();
        }
    }

    public int SamplesToNextArpEvent() => ArpActive ? Arp.SamplesToNextEvent : int.MaxValue;
    public void AdvanceArp(int samples) { if (ArpActive) Arp.Advance(samples); }

    // ------------------------------------------------------------------ internals

    private int Find(int note)
    {
        for (int i = 0; i < _n; i++) if (_e[i].Note == note) return i;
        return -1;
    }

    private void RemoveAt(int i)
    {
        for (int j = i + 1; j < _n; j++) _e[j - 1] = _e[j];
        _n--;
    }

    private float GlideMs() => ParamCurves.GlideMs(_params.Get(_layer, ParamId.Glide));

    private void Press(int note, int velocity, bool down)
    {
        int aliveBefore = AliveCount;
        int i = Find(note);
        var entry = new Entry { Note = note, Vel = velocity, Down = down };
        if (i >= 0)
        {
            entry.Latched = _e[i].Latched;
            entry.Down |= _e[i].Down;
            RemoveAt(i);
        }
        if (_n == _e.Length) RemoveAt(0);          // overflow: forget the oldest
        _e[_n++] = entry;

        switch (_mode)
        {
            case VoiceMode.Poly:
            case VoiceMode.Unison:
                _sink.PolyOn(note, velocity);
                break;
            case VoiceMode.Mono:
                _monoNote = note;
                _sink.MonoStart(note, velocity, GlideMs(), true);
                break;
            case VoiceMode.Legato:
                _monoNote = note;
                _sink.MonoStart(note, velocity, GlideMs(), retrigger: aliveBefore == 0);
                break;
            case VoiceMode.Arp:
                PushArpNotes();
                if (!Arp.Running) Arp.Start();
                break;
        }
    }

    /// <summary>An entry lost a reason to live: drop it if dead and let the mode react.</summary>
    private void AfterRelease(int note, int index)
    {
        if (_e[index].Alive) return;
        RemoveAt(index);
        if (IsPolyLike) _sink.PolyOff(note);
        else Reconcile(poly: false);
    }

    /// <summary>Removes dead entries and brings the voices in line (poly: release their notes).</summary>
    private void Reconcile(bool poly)
    {
        for (int i = _n - 1; i >= 0; i--)
        {
            if (_e[i].Alive) continue;
            int note = _e[i].Note;
            RemoveAt(i);
            if (poly && IsPolyLike) _sink.PolyOff(note);
        }
        if (IsPolyLike) return;
        if (_mode == VoiceMode.Arp)
        {
            if (_n == 0) { Arp.Stop(); _sink.MonoStop(); }
            else PushArpNotes();
            return;
        }
        // MONO / LEGATO: follow the newest living entry
        if (_n == 0)
        {
            if (_monoNote >= 0) _sink.MonoStop();
            _monoNote = -1;
            return;
        }
        ref var top = ref _e[_n - 1];
        if (top.Note != _monoNote)
        {
            _monoNote = top.Note;
            _sink.MonoStart(top.Note, top.Vel, GlideMs(), retrigger: _mode == VoiceMode.Mono);
        }
    }

    private void PushArpNotes()
    {
        for (int i = 0; i < _n; i++) { _tmpNotes[i] = _e[i].Note; _tmpVels[i] = _e[i].Vel; }
        Arp.SetNotes(_tmpNotes.AsSpan(0, _n), _tmpVels.AsSpan(0, _n));
    }

    private void ChangeMode(VoiceMode m)
    {
        _sink.ReleaseAll();
        Arp.Stop();
        _monoNote = -1;
        for (int i = _n - 1; i >= 0; i--)
        {
            _e[i].Pedal = false;
            if (!_e[i].Alive) RemoveAt(i);
        }
        _mode = m;
        // re-feed the keys that are still held / latched
        int count = _n;
        for (int i = 0; i < count; i++)
        {
            var en = _e[i];
            switch (m)
            {
                case VoiceMode.Poly:
                case VoiceMode.Unison:
                    _sink.PolyOn(en.Note, en.Vel);
                    break;
                case VoiceMode.Mono:
                case VoiceMode.Legato:
                    if (i == count - 1)
                    {
                        _monoNote = en.Note;
                        _sink.MonoStart(en.Note, en.Vel, 0f, true);
                    }
                    break;
                case VoiceMode.Arp:
                    break;
            }
        }
        if (m == VoiceMode.Arp && count > 0) { PushArpNotes(); Arp.Start(); }
    }
}
