using Sambient.Dsp.Wavetables;
using Sambient.Engine;

namespace Sambient.Tests;

public class EventQueueTests
{
    [Fact]
    public void MultiProducer_NoLostEvents_PerProducerOrderKept()
    {
        const int producers = 4, perProducer = 50_000;
        var q = new EngineEventQueue(1024);
        var enqueued = new long[producers];
        long received = 0;
        var lastSeq = new long[producers];
        Array.Fill(lastSeq, -1);
        bool orderOk = true;
        int doneProducers = 0;

        void Consume(EngineEvent ev)
        {
            received++;
            int p = ev.Layer;
            long seq = (long)ev.Value;
            if (seq <= lastSeq[p]) orderOk = false;
            lastSeq[p] = seq;
        }
        var consumer = new Thread(() =>
        {
            while (true)
            {
                if (q.TryDequeue(out var ev)) { Consume(ev); continue; }
                if (Volatile.Read(ref doneProducers) == producers)
                {
                    while (q.TryDequeue(out ev)) Consume(ev);   // final drain after all producers finished
                    break;
                }
            }
        });
        consumer.Start();
        var threads = new Thread[producers];
        for (int p = 0; p < producers; p++)
        {
            int id = p;
            threads[p] = new Thread(() =>
            {
                for (int i = 0; i < perProducer; i++)
                {
                    var ev = new EngineEvent { Type = EngineEventType.NoteOn, Layer = (byte)id, Value = i };
                    // retry until accepted: drops are then expected to equal the failed attempts
                    while (!q.TryEnqueue(in ev)) Thread.Yield();
                    enqueued[id]++;
                }
                Interlocked.Increment(ref doneProducers);
            });
            threads[p].Start();
        }
        foreach (var t in threads) t.Join();
        consumer.Join();

        Assert.Equal((long)producers * perProducer, received);
        Assert.True(orderOk, "events of one producer arrived out of order");
    }

    [Fact]
    public void UnderCapacity_FromTwoThreads_AllEventsArrive_NoDrops()
    {
        var q = new EngineEventQueue(1024);
        var threads = Enumerable.Range(0, 2).Select(p => new Thread(() =>
        {
            for (int i = 0; i < 500; i++) q.TryEnqueue(new EngineEvent { Layer = (byte)p, Value = i });
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        int n = 0;
        while (q.TryDequeue(out _)) n++;
        Assert.Equal(1000, n);
        Assert.Equal(0, q.Dropped);
    }

    [Fact]
    public void Overflow_DropsAndCounts()
    {
        var q = new EngineEventQueue(8);
        int ok = 0;
        for (int i = 0; i < 12; i++) if (q.TryEnqueue(new EngineEvent { Value = i })) ok++;
        Assert.Equal(8, ok);
        Assert.Equal(4, q.Dropped);
        int n = 0;
        while (q.TryDequeue(out var ev)) { Assert.Equal(n, (int)ev.Value); n++; }
        Assert.Equal(8, n);
        // usable again after draining
        Assert.True(q.TryEnqueue(new EngineEvent()));
    }
}

public class AudioEngineTests
{
    private const int Fs = 48000;
    private static readonly WavetableBank SharedBank = MakeBank();

    private static WavetableBank MakeBank()
    {
        var b = new WavetableBank();
        foreach (int w in new[] { 1, 2, 3 }) b.EnsureBuiltAsync(w).GetAwaiter().GetResult();
        return b;
    }

    private static AudioEngine Make()
    {
        var e = new AudioEngine(Fs, new ParameterStore(), SharedBank);
        e.Parameters.Set(0, ParamId.Attack, 0);
        e.Parameters.Set(0, ParamId.Release, 20);
        e.Parameters.Set(0, ParamId.Wave, 1);
        return e;
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

    private static float Peak(float[] a, int from = 0)
    {
        float p = 0;
        for (int i = from; i < a.Length; i++) p = Math.Max(p, Math.Abs(a[i]));
        return p;
    }

    [Fact]
    public void NoteOn_ProducesSound_AndSilenceAfterRelease()
    {
        var e = Make();
        e.NoteOn(0, 60, 100);
        var a = Run(e, Fs / 4);
        Assert.True(Peak(a) > 0.02f);
        Assert.Equal(1, e.ActiveVoices);
        Assert.True(e.PeakLeft > 0.01f);

        e.NoteOff(0, 60);
        Run(e, Fs / 2);                 // release knob 20 -> well under 100 ms to -60 dB
        var tail = Run(e, Fs / 4);
        Assert.True(Peak(tail) < 1e-4f);
        Assert.Equal(0, e.ActiveVoices);
    }

    [Fact]
    public void VelocityZeroUsesFixedVelocityParam()
    {
        var soft = Make(); soft.SetParam(0, ParamId.Velocity, 20); soft.NoteOn(0, 60, 0);
        var loud = Make(); loud.SetParam(0, ParamId.Velocity, 127); loud.NoteOn(0, 60, 0);
        Assert.True(Peak(Run(loud, 4800)) > 3f * Peak(Run(soft, 4800)));
    }

    [Fact]
    public void SameNoteRetrigger_ReusesVoice()
    {
        var e = Make();
        e.NoteOn(0, 60, 100); Run(e, 1000);
        e.NoteOn(0, 60, 100); Run(e, 1000);
        Assert.Equal(1, e.ActiveVoices);
        Assert.Equal(1, e.GetLayer(0).VoicesForNote(60));
    }

    [Fact]
    public void FullLayer_StealsOldestHeldVoice()
    {
        var e = Make();
        for (int i = 0; i < 16; i++) e.NoteOn(0, 40 + i, 100);
        Run(e, 512);
        Assert.Equal(16, e.ActiveVoices);
        e.NoteOn(0, 90, 100);
        Run(e, 2048);
        var l = e.GetLayer(0);
        Assert.Equal(16, e.ActiveVoices);
        Assert.Equal(0, l.VoicesForNote(40));      // oldest held was taken over
        Assert.Equal(1, l.VoicesForNote(90));
        Assert.Equal(1, l.VoicesForNote(41));
    }

    [Fact]
    public void FullLayer_PrefersStealingReleasingVoice()
    {
        var e = Make();
        e.SetParam(0, ParamId.Release, 120);        // long release so the voice is still releasing when stolen
        for (int i = 0; i < 16; i++) e.NoteOn(0, 40 + i, 100);
        Run(e, 512);
        e.NoteOff(0, 50);
        Run(e, 256);
        e.NoteOn(0, 90, 100);
        Run(e, 2048);
        var l = e.GetLayer(0);
        Assert.Equal(1, l.VoicesForNote(40));       // oldest held survives
        Assert.Equal(0, l.VoicesForNote(50));       // the releasing one was stolen
        Assert.Equal(1, l.VoicesForNote(90));
    }

    [Fact]
    public void Sustain_DefersNoteOff_UntilPedalUp()
    {
        var e = Make();
        e.Sustain(true);
        e.NoteOn(0, 60, 100);
        Run(e, 2000);
        e.NoteOff(0, 60);
        var held = Run(e, Fs / 2);
        Assert.True(Peak(held, held.Length / 2) > 0.02f, "note must keep sounding under the pedal");
        Assert.Equal(1, e.ActiveVoices);

        e.Sustain(false);
        Run(e, Fs / 2);
        var tail = Run(e, 4800);
        Assert.True(Peak(tail) < 1e-4f);
        Assert.Equal(0, e.ActiveVoices);
    }

    [Fact]
    public void AllNotesOff_ReleasesEverything()
    {
        var e = Make();
        for (int i = 0; i < 5; i++) e.NoteOn(0, 50 + i, 100);
        Run(e, 2000);
        e.AllNotesOff();
        Run(e, Fs);
        Assert.Equal(0, e.ActiveVoices);
    }

    [Theory]
    [InlineData(FilterType.Off)]
    [InlineData(FilterType.LowPass)]
    [InlineData(FilterType.HighPass)]
    [InlineData(FilterType.BandPass)]
    public void NoNaN_AllFiltersExtremeSettings(FilterType ft)
    {
        var e = Make();
        e.SetParam(0, ParamId.FilterType, (float)ft);
        e.SetParam(0, ParamId.Resonance, 127);
        e.NoteOn(0, 36, 127); e.NoteOn(0, 100, 127); e.NoteOn(0, 72, 1);
        e.PitchBend(1f); e.ModWheel(1f);
        var rng = new Random(1);
        for (int i = 0; i < 100; i++)
        {
            e.SetParam(0, ParamId.Cutoff, rng.Next(128));
            e.SetParam(0, ParamId.Harmonic, rng.Next(128));
            var a = Run(e, 1024);
            foreach (var s in a) Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1f);
        }
    }

    [Fact]
    public void SixteenLoudVoices_StayWithinFullScale()
    {
        var e = Make();
        e.SetParam(0, ParamId.FilterType, 0);
        e.SetParam(0, ParamId.Level, 127);
        e.SetGlobal(GlobalParamId.MasterVolume, 127);
        for (int i = 0; i < 16; i++) e.NoteOn(0, 36 + i * 3, 127);
        var a = Run(e, Fs / 2);
        float p = Peak(a);
        Assert.True(p <= 1f, $"peak {p}");
        Assert.True(p > 0.5f);
        Assert.Equal(16, e.ActiveVoices);
    }

    [Fact]
    public void SoftClip_IsMonotonicAndBounded()
    {
        float prev = -2f;
        for (float x = -50f; x <= 50f; x += 0.01f)
        {
            float y = AudioEngine.SoftClip(x);
            Assert.InRange(y, -1f, 1f);
            Assert.True(y >= prev - 1e-6f);
            prev = y;
        }
        Assert.Equal(0.3f, AudioEngine.SoftClip(0.3f));
        Assert.Equal(0f, AudioEngine.SoftClip(float.NaN));
    }

    [Fact]
    public async Task Render_AllocatesNothing_AfterWarmup()
    {
        var e = Make();
        await SharedBank.EnsureBuiltAsync(2);
        var buf = new float[256 * 2];
        void Iteration(int i)
        {
            e.NoteOn(0, 40 + (i % 30), 90);
            if (i % 3 == 0) e.NoteOff(0, 40 + ((i + 5) % 30));
            e.Parameters.Set(0, ParamId.Cutoff, i % 128);
            e.Parameters.Set(0, ParamId.Harmonic, (i * 7) % 128);
            e.Parameters.Set(0, ParamId.FilterType, 1 + i % 3);
            if (i % 50 == 0) e.Parameters.Set(0, ParamId.Wave, i % 100 == 0 ? 1 : 2);   // prebuilt: swap path, no build request
            if (i == 100) e.Sustain(true);
            if (i == 200) e.Sustain(false);
            e.PitchBend((i % 10) / 10f);
            e.Render(buf, 256);
        }
        for (int i = 0; i < 200; i++) Iteration(i);        // warm-up (JIT, tiered compilation)
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 200; i < 1200; i++) Iteration(i);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    private static float MaxStep(float[] interleaved, int fromFrame, int toFrame)
    {
        float m = 0;
        for (int i = fromFrame + 1; i < toFrame; i++)
            m = Math.Max(m, Math.Abs(interleaved[2 * i] - interleaved[2 * (i - 1)]));
        return m;
    }

    [Fact]
    public void WaveSwapWhilePlaying_HasNoLargeSampleStep()
    {
        var e = Make();
        e.SetParam(0, ParamId.FilterType, 0);
        e.SetParam(0, ParamId.Harmonic, 0);
        e.SetParam(0, ParamId.Wave, 3);
        e.NoteOn(0, 45, 100);
        Run(e, Fs / 2);
        var sine = Run(e, Fs / 5);
        float refSine = MaxStep(sine, 0, Fs / 5);

        e.Parameters.Set(0, ParamId.Wave, 1);
        var swap = Run(e, Fs / 2);
        float swapStep = MaxStep(swap, 0, Fs / 2);

        var saw = Run(e, Fs / 5);
        float refSaw = MaxStep(saw, 0, Fs / 5);
        Assert.True(swapStep <= Math.Max(refSine, refSaw) * 1.25f + 0.01f,
            $"swap step {swapStep}, sine ref {refSine}, saw ref {refSaw}");
    }

    [Fact]
    public async Task WaveNotReady_KeepsOldWave_ThenSwapsWhenBuilt()
    {
        var bank = new WavetableBank();
        await bank.EnsureBuiltAsync(3);
        var e = new AudioEngine(Fs, new ParameterStore(), bank);
        e.SetParam(0, ParamId.Wave, 3);
        Run(e, 256);
        var first = e.GetLayer(0).CurrentTable;
        Assert.Equal(3, first!.Index);
        e.SetParam(0, ParamId.Wave, 8);           // requests build; may not be ready immediately
        await bank.EnsureBuiltAsync(8);
        Run(e, 256);
        Assert.Equal(8, e.GetLayer(0).CurrentTable!.Index);
    }
}

public class HeldNoteTrackerTests
{
    [Fact]
    public void TracksHeldAndSustainedNotes()
    {
        var t = new HeldNoteTracker();
        int changes = 0;
        t.Changed += () => changes++;
        t.NoteOn(60);
        Assert.True(t.IsSounding(60));
        t.SetSustain(true);
        t.NoteOff(60);
        Assert.False(t.IsHeld(60));
        Assert.True(t.IsSounding(60));
        t.SetSustain(false);
        Assert.False(t.IsSounding(60));
        t.NoteOn(61); t.Clear();
        Assert.False(t.IsSounding(61));
        Assert.True(changes >= 5);
    }
}
