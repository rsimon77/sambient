namespace Sambient.Seq;

/// <summary>One entry of a song chain: a palette slot (0-based bank 0..7, slot 0..15).</summary>
public readonly record struct ChainSlot(int Bank, int Slot)
{
    public bool IsValid => (uint)Bank < PatternBank.Banks && (uint)Slot < PatternBank.SlotsPerBank;
}

/// <summary>Project-level settings (everything that is saved in the project but is not part of a palette).</summary>
public sealed record ProjectSettings
{
    /// <summary>PTN: loading a palette loads its BPM; GLBL: the current BPM is kept.</summary>
    public BpmMode BpmMode { get; init; } = BpmMode.Pattern;
    /// <summary>R.RST: reset the reverb on every palette change.</summary>
    public bool ResetReverbOnChange { get; init; }
    /// <summary>SND / SEQ mute mode.</summary>
    public MuteMode MuteMode { get; init; } = MuteMode.Sound;
    public int MetronomeVolume { get; init; } = 8;
    public int PreCount { get; init; } = 2;
    /// <summary>Master tune in cents (-75..75).</summary>
    public float MasterTuneCents { get; init; }

    public ProjectSettings Normalized() => this with
    {
        MetronomeVolume = Math.Clamp(MetronomeVolume, 0, 15),
        PreCount = Math.Clamp(PreCount, 0, 8),
        MasterTuneCents = float.IsFinite(MasterTuneCents) ? Math.Clamp(MasterTuneCents, -75f, 75f) : 0f,
        BpmMode = Enum.IsDefined(BpmMode) ? BpmMode : BpmMode.Pattern,
        MuteMode = Enum.IsDefined(MuteMode) ? MuteMode : MuteMode.Sound,
    };
}

/// <summary>
/// A song arrangement: an ordered list of palette slots with the chain playback options (loop, repeats per palette, optional
/// Relaxation mode with fade). Value semantics through <see cref="ValueEquals"/>; <see cref="Project"/> hands out copies.
/// </summary>
public sealed class SongChain
{
    public string Name { get; set; } = "Song";
    public List<ChainSlot> Entries { get; set; } = new();
    public bool Loop { get; set; }
    /// <summary>Pattern cycles per palette (1..99; Relaxation 2..10).</summary>
    public int Repeats { get; set; } = 1;
    public bool Relaxation { get; set; }
    /// <summary>Relaxation fade-out time in seconds (5..30 in steps of 5).</summary>
    public int FadeSeconds { get; set; } = 10;
    /// <summary>Fade-in time after a switch; 0 = same as the fade-out time.</summary>
    public int FadeInSeconds { get; set; }

    public SongChain Clone() => new()
    {
        Name = Name, Entries = new List<ChainSlot>(Entries), Loop = Loop, Repeats = Repeats, Relaxation = Relaxation,
        FadeSeconds = FadeSeconds, FadeInSeconds = FadeInSeconds,
    };

    public bool ValueEquals(SongChain? o)
        => o != null && Name == o.Name && Loop == o.Loop && Repeats == o.Repeats && Relaxation == o.Relaxation
           && FadeSeconds == o.FadeSeconds && FadeInSeconds == o.FadeInSeconds && Entries.SequenceEqual(o.Entries);

    /// <summary>Clamps the numeric options to what the sequencer accepts.</summary>
    public SongChain Normalized()
    {
        var c = Clone();
        c.Name = string.IsNullOrWhiteSpace(Name) ? "Song" : Name.Trim();
        c.Entries = Entries.Where(e => e.IsValid).ToList();
        c.Repeats = c.Relaxation ? Math.Clamp(Repeats, 2, 10) : Math.Clamp(Repeats, 1, 99);
        c.FadeSeconds = Math.Clamp((int)Math.Round(FadeSeconds / 5.0) * 5, 5, 30);
        c.FadeInSeconds = Math.Clamp(FadeInSeconds, 0, 60);
        return c;
    }
}
