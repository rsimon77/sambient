using Sambient.Dsp.Effects;
using Sambient.Dsp.Wavetables;
using Sambient.Engine;

namespace Sambient.Tests;

/// <summary>PTN LVL (pattern level) and FX routing PRE / POST, as described in the original's manual.</summary>
public class PatternLevelRoutingTests
{
    private const int Fs = 48000;
    private static readonly WavetableBank SharedBank = MakeBank();

    private static WavetableBank MakeBank()
    {
        var b = new WavetableBank();
        b.EnsureBuiltAsync(1).GetAwaiter().GetResult();
        return b;
    }

    /// <summary>One SAW.S layer through a long tape delay, reverb off.</summary>
    private static AudioEngine Make(bool fxPre)
    {
        var p = new ParameterStore();
        p.Set(0, ParamId.Wave, 1);
        p.Set(0, ParamId.Attack, 0);
        p.Set(0, ParamId.Release, 0);
        p.Set(0, ParamId.FilterType, 0);
        p.Set(GlobalParamId.ReverbType, (float)ReverbType.Off);
        p.Set(GlobalParamId.FxType, (float)MasterFxType.TapeDelay);
        p.Set(GlobalParamId.FxSpeed, 70);    // a few hundred ms
        p.Set(GlobalParamId.FxAmount, 110);  // long feedback tail
        p.Set(GlobalParamId.FxRouting, fxPre ? 1 : 0);
        return new AudioEngine(Fs, p, SharedBank);
    }

    private static double Rms(AudioEngine e, int frames)
    {
        var buf = new float[512];
        double sum = 0;
        for (int done = 0; done < frames; done += 256)
        {
            e.Render(buf, 256);
            foreach (var x in buf) sum += x * x;
        }
        return Math.Sqrt(sum / (frames * 2.0));
    }

    [Fact]
    public void DefaultPatternLevel_IsUnityGain()
    {
        Assert.Equal(63.5f, new ParameterStore().Get(GlobalParamId.PatternLevel));
        Assert.Equal(1f, Sambient.Dsp.Params.ParamCurves.LevelGain(63.5f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PatternLevelZero_SilencesDirectSound(bool fxPre)
    {
        var e = Make(fxPre);
        e.SetGlobal(GlobalParamId.FxType, (float)MasterFxType.Off);
        e.SetGlobal(GlobalParamId.PatternLevel, 0);
        e.NoteOn(0, 60, 120);
        Rms(e, Fs / 10);                       // let the 20 ms level smoothing settle
        Assert.True(Rms(e, Fs / 5) < 1e-5, "pattern level 0 must silence the layers");
    }

    [Fact]
    public void PostRouting_KeepsFxTail_PreRouting_CutsIt()
    {
        double TailAfterLevelZero(bool fxPre)
        {
            var e = Make(fxPre);
            e.NoteOn(0, 60, 120);
            Rms(e, Fs / 4);                    // fill the delay line
            e.NoteOff(0, 60);
            e.SetGlobal(GlobalParamId.PatternLevel, 0);
            Rms(e, Fs / 20);                   // smoothing + note release
            return Rms(e, Fs / 2);             // what is left: the delay tail (POST) or silence (PRE)
        }

        double post = TailAfterLevelZero(fxPre: false);
        double pre = TailAfterLevelZero(fxPre: true);
        Assert.True(post > 1e-3, $"POST: FX tail should survive PTN LVL 0 (rms {post})");
        Assert.True(pre < 1e-5, $"PRE: PTN LVL 0 should silence everything (rms {pre})");
    }
}
