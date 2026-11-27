using Sambient.Control;
using Xunit;

namespace Sambient.Control.Tests;

public class MidiMapTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "sambient-test-" + Guid.NewGuid().ToString("N"), "midimap.json");

    [Fact]
    public void Defaults_MapCc21To28()
    {
        var m = MidiMap.CreateDefault();
        Assert.True(m.TryResolve(ControllerSource.Keys, 5, 21, out var t));
        Assert.Equal("Harmonic", t);
        Assert.True(m.TryResolve(ControllerSource.Daw, 0, 28, out t));
        Assert.Equal("Velocity", t);
        Assert.False(m.TryResolve(ControllerSource.Keys, 0, 40, out _));
    }

    [Fact]
    public void MissingFile_IsCreatedWithDefaults_AndRoundTrips()
    {
        var path = Tmp();
        var m = MidiMap.LoadOrCreate(path);
        Assert.True(File.Exists(path));
        m.Bind(MapSource.Keys, 3, 74, "Cutoff");
        m.Save(path);
        var m2 = MidiMap.LoadOrCreate(path);
        Assert.True(m2.TryResolve(ControllerSource.Keys, 2, 74, out var t));   // channel 3 (1-based) == event channel 2
        Assert.Equal("Cutoff", t);
        Assert.False(m2.TryResolve(ControllerSource.Keys, 0, 74, out _));
        Assert.False(m2.TryResolve(ControllerSource.Daw, 2, 74, out _));
        Assert.Equal(3, m2.Find("Cutoff")!.Channel);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var m = MidiMap.LoadOrCreate(path);
        Assert.Equal(8, m.Bindings.Count);
    }

    [Fact]
    public void Learn_BindsNextCc_ReplacingOldBindingOfTarget()
    {
        var m = MidiMap.CreateDefault();
        var learn = new MidiLearn(m);
        learn.Arm("Cutoff");
        Assert.Null(learn.TryCapture(new ControllerCc(ControllerSource.Keys, 0, 64, 127)));   // sustain is ignored
        Assert.Equal("Cutoff", learn.TryCapture(new ControllerCc(ControllerSource.Keys, 0, 74, 10)));
        Assert.Null(learn.ArmedTarget);
        Assert.False(m.TryResolve(ControllerSource.Keys, 0, 22, out _));     // old CC22 -> Cutoff removed
        Assert.True(m.TryResolve(ControllerSource.Keys, 0, 74, out var t));
        Assert.Equal("Cutoff", t);
        m.Clear("Cutoff");
        Assert.False(m.TryResolve(ControllerSource.Keys, 0, 74, out _));
    }

    [Fact]
    public void NewTargets_RoundTrip_AndOldFilesStillLoad()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // a file written by M1 (no M2 targets)
        File.WriteAllText(path, "{ \"version\": 1, \"bindings\": [ { \"source\": \"Any\", \"channel\": null, \"cc\": 21, \"target\": \"Harmonic\" } ] }");
        var m = MidiMap.LoadOrCreate(path);
        Assert.True(m.TryResolve(ControllerSource.Keys, 0, 21, out var t));
        Assert.Equal("Harmonic", t);

        m.Bind(MapSource.Any, null, 30, MidiTargets.ReverbSend);
        m.Bind(MapSource.Any, null, 31, MidiTargets.ShimmerSend);
        m.Bind(MapSource.Any, null, 32, MidiTargets.FxAmount);
        m.Save(path);
        var m2 = MidiMap.LoadOrCreate(path);
        Assert.True(m2.TryResolve(ControllerSource.Daw, 0, 30, out t)); Assert.Equal("ReverbSend", t);
        Assert.True(m2.TryResolve(ControllerSource.Daw, 0, 31, out t)); Assert.Equal("ShimmerSend", t);
        Assert.True(m2.TryResolve(ControllerSource.Daw, 0, 32, out t)); Assert.Equal("FxAmount", t);
        Assert.True(MidiTargets.IsLayerTarget("ReverbSend"));
        Assert.False(MidiTargets.IsLayerTarget("ReverbMix"));
    }
}
