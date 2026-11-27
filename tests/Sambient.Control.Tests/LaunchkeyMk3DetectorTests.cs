using Sambient.Control;
using Xunit;

namespace Sambient.Control.Tests;

public class LaunchkeyMk3DetectorTests
{
    private static LaunchkeyPorts D(string[] ins, string[] outs) => LaunchkeyMk3Detector.Detect(ins, outs);

    [Fact]
    public void Mk3_49_FullSet_IsComplete()
    {
        var r = D(
            new[] { "Launchkey MK3 49 LKMK3 MIDI Out", "Launchkey MK3 49 LKMK3 DAW Out" },
            new[] { "Launchkey MK3 49 LKMK3 MIDI In", "Launchkey MK3 49 LKMK3 DAW In" });
        Assert.True(r.IsComplete);
        Assert.Equal(49, r.Model);
        Assert.Equal("Launchkey MK3 49 LKMK3 MIDI Out", r.MidiIn);
        Assert.Equal("Launchkey MK3 49 LKMK3 DAW Out", r.DawIn);
        Assert.Equal("Launchkey MK3 49 LKMK3 MIDI In", r.MidiOut);
        Assert.Equal("Launchkey MK3 49 LKMK3 DAW In", r.DawOut);
    }

    [Fact]
    public void Mk3_FullSet_WithUnrelatedDevices_Mixed()
    {
        var r = D(
            new[] { "IAC Driver Bus 1", "Launchkey MK3 49 LKMK3 DAW Out", "Launchkey MK3 49 LKMK3 MIDI Out", "Arturia KeyStep" },
            new[] { "IAC Driver Bus 1", "Launchkey MK3 49 LKMK3 MIDI In", "Launchkey MK3 49 LKMK3 DAW In" });
        Assert.True(r.IsComplete);
        Assert.Equal(49, r.Model);
    }

    [Fact]
    public void Mk3_WithoutModelNumber()
    {
        var r = D(
            new[] { "Launchkey MK3 LKMK3 MIDI Out", "Launchkey MK3 LKMK3 DAW Out" },
            new[] { "Launchkey MK3 LKMK3 MIDI In", "Launchkey MK3 LKMK3 DAW In" });
        Assert.True(r.IsComplete);
        Assert.Null(r.Model);
    }

    [Fact]
    public void Mk3_61()
    {
        var r = D(
            new[] { "Launchkey MK3 61 LKMK3 MIDI Out", "Launchkey MK3 61 LKMK3 DAW Out" },
            new[] { "Launchkey MK3 61 LKMK3 MIDI In", "Launchkey MK3 61 LKMK3 DAW In" });
        Assert.True(r.IsComplete);
        Assert.Equal(61, r.Model);
    }

    [Theory]
    [InlineData("Launchkey MK3 25 LKMK3 MIDI Out", 25)]
    [InlineData("Launchkey MK3 37 LKMK3 MIDI Out", 37)]
    [InlineData("Launchkey MK3 88 LKMK3 MIDI Out", 88)]
    [InlineData("Launchkey MK3 49 LKMK3 MIDI Out", 49)]
    [InlineData("Launchkey MK3 61 LKMK3 MIDI Out", 61)]
    public void ModelParsing(string name, int model)
    {
        var r = D(new[] { name }, Array.Empty<string>());
        Assert.Equal(model, r.Model);
        Assert.Equal(name, r.MidiIn);
    }

    [Fact]
    public void LowercaseAndExtraSpacing()
    {
        var r = D(
            new[] { "  launchkey   mk3  49   lkmk3  midi  out ", "LAUNCHKEY MK3 49 LKMK3 DAW OUT" },
            new[] { "launchkey mk3 49 lkmk3 midi in", "Launchkey  MK3 49  LKMK3  DAW In" });
        Assert.True(r.IsComplete);
        Assert.Equal(49, r.Model);
    }

    [Fact]
    public void NameVariants_Punctuation_And_MkSpace()
    {
        var r = D(
            new[] { "Launchkey-MK3-49 (MIDI)", "Launchkey MK 3 49 DAW" },
            new[] { "Launchkey_MK3_49_MIDI", "Launchkey MK3 49 [DAW]" });
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void OnlyLkmk3Token()
    {
        var r = D(new[] { "LKMK3 MIDI Out", "LKMK3 DAW Out" }, new[] { "LKMK3 MIDI In", "LKMK3 DAW In" });
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void WindowsStyle_Midiin2_IsDaw()
    {
        var r = D(
            new[] { "Launchkey MK3 49", "MIDIIN2 (Launchkey MK3 49)" },
            new[] { "Launchkey MK3 49", "MIDIOUT2 (Launchkey MK3 49)" });
        Assert.True(r.IsComplete);
        Assert.Equal("MIDIIN2 (Launchkey MK3 49)", r.DawIn);
        Assert.Equal("Launchkey MK3 49", r.MidiIn);
    }

    [Theory]
    [InlineData("Launchkey Mini MK3 MIDI Out")]
    [InlineData("Launchkey Mini MK3 DAW Out")]
    [InlineData("Launchkey Mini [MK3] LKMK3 MIDI Out")]
    [InlineData("LaunchkeyMini MK3 MIDI Out")]
    [InlineData("Launchkey Mini MK3 49 MIDI Out")]
    public void LaunchkeyMiniMk3_DoesNotMatch(string name)
    {
        var r = D(new[] { name }, new[] { name.Replace("Out", "In") });
        Assert.False(r.IsAny);
    }

    [Theory]
    [InlineData("Launchkey 49 MIDI Out")]
    [InlineData("Launchkey MK2 49 MIDI Out")]
    [InlineData("Launchkey 49 MK2 DAW Out")]
    [InlineData("Launchkey MK4 49 MIDI Out")]
    [InlineData("Launchkey MK4 49 DAW Out")]
    [InlineData("Launchkey Mini MK2 MIDI Out")]
    [InlineData("Launchkey 61 InControl")]
    public void OtherGenerations_DoNotMatch(string name)
    {
        var r = D(new[] { name }, new[] { name });
        Assert.False(r.IsAny);
    }

    [Fact]
    public void UnrelatedDevices_NoMatch()
    {
        var names = new[] { "IAC Driver Bus 1", "Arturia KeyStep 37", "Novation Circuit Tracks", "Komplete Kontrol M32", "Network Session 1", "MPK mini 3", "Launch Control XL" };
        var r = D(names, names);
        Assert.False(r.IsAny);
        Assert.False(r.IsComplete);
        Assert.Null(r.Model);
    }

    [Fact]
    public void EmptyLists_NoMatch()
    {
        var r = D(Array.Empty<string>(), Array.Empty<string>());
        Assert.False(r.IsAny);
        Assert.Equal(LaunchkeyPorts.None, r);
    }

    [Fact]
    public void OnlyMidiPortsPresent_NotComplete()
    {
        var r = D(new[] { "Launchkey MK3 49 LKMK3 MIDI Out" }, new[] { "Launchkey MK3 49 LKMK3 MIDI In" });
        Assert.False(r.IsComplete);
        Assert.NotNull(r.MidiIn);
        Assert.NotNull(r.MidiOut);
        Assert.Null(r.DawIn);
        Assert.Null(r.DawOut);
    }

    [Fact]
    public void OnlyDawPortsPresent_NotComplete()
    {
        var r = D(new[] { "Launchkey MK3 49 LKMK3 DAW Out" }, new[] { "Launchkey MK3 49 LKMK3 DAW In" });
        Assert.False(r.IsComplete);
        Assert.Null(r.MidiIn);
        Assert.NotNull(r.DawIn);
        Assert.NotNull(r.DawOut);
    }

    [Fact]
    public void MissingOneOutput_NotComplete()
    {
        var r = D(
            new[] { "Launchkey MK3 49 LKMK3 MIDI Out", "Launchkey MK3 49 LKMK3 DAW Out" },
            new[] { "Launchkey MK3 49 LKMK3 MIDI In" });
        Assert.False(r.IsComplete);
        Assert.Null(r.DawOut);
    }

    [Fact]
    public void MiniAndFullPlugged_PicksFullOnly()
    {
        var r = D(
            new[] { "Launchkey Mini MK3 MIDI Out", "Launchkey Mini MK3 DAW Out", "Launchkey MK3 49 LKMK3 MIDI Out", "Launchkey MK3 49 LKMK3 DAW Out" },
            new[] { "Launchkey Mini MK3 MIDI In", "Launchkey Mini MK3 DAW In", "Launchkey MK3 49 LKMK3 MIDI In", "Launchkey MK3 49 LKMK3 DAW In" });
        Assert.True(r.IsComplete);
        Assert.Equal(49, r.Model);
        Assert.DoesNotContain("Mini", r.MidiIn);
        Assert.DoesNotContain("Mini", r.DawOut);
    }

    [Fact]
    public void PortInfoOverload_UsesDirection()
    {
        var ports = new[]
        {
            new MidiPortInfo("Launchkey MK3 49 LKMK3 MIDI Out", MidiPortDirection.In, MidiPortKind.Midi),
            new MidiPortInfo("Launchkey MK3 49 LKMK3 DAW In", MidiPortDirection.Out, MidiPortKind.Daw),
        };
        var r = LaunchkeyMk3Detector.Detect(ports);
        Assert.Equal("Launchkey MK3 49 LKMK3 MIDI Out", r.MidiIn);
        Assert.Equal("Launchkey MK3 49 LKMK3 DAW In", r.DawOut);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankNames_Ignored(string? name)
    {
        Assert.False(LaunchkeyMk3Detector.TryParse(name, out _, out _));
    }

    [Fact]
    public void PortWatcher_RaisesConnectAndDisconnect()
    {
        var present = false;
        using var w = new PortWatcher(listPorts: () => present
            ? new[] { new MidiPortInfo("Launchkey MK3 49 LKMK3 MIDI Out", MidiPortDirection.In, MidiPortKind.Midi) }
            : Array.Empty<MidiPortInfo>());
        int c = 0, d = 0;
        w.LaunchkeyConnected += _ => c++;
        w.LaunchkeyDisconnected += _ => d++;
        w.Poll(); Assert.Equal((0, 0), (c, d));
        present = true;
        w.Poll(); w.Poll(); Assert.Equal((1, 0), (c, d));
        present = false;
        w.Poll(); Assert.Equal((1, 1), (c, d));
    }
}
