namespace Sambient.Engine;

/// <summary>
/// Per-layer tuning table: MIDI note (0..127) -> tuned pitch expressed as a FRACTIONAL MIDI NOTE on the 12-TET A4=440 grid
/// (<c>69 + 12*log2(f/440)</c>). Voices use it in place of the integer note, so glide runs in log-frequency between the two
/// tuned pitches and every other offset (octave, bend, PITCH, detune, unison, TUNE LFO, TUNE ALL) is simply added in
/// semitones. EQUA with master tune 0 gives exactly <c>table[n] = n</c> (bit-identical to the old behaviour).
/// <para>
/// Ratio tables (relative to the tonic, which keeps its 12-TET pitch; tonic = key 0..11 = C, Db, D, Eb, E, F, Gb, G, Ab, A, Bb, B):
/// <list type="bullet">
/// <item>PR.MJ just major (5-limit, symmetric): 1, 16/15, 9/8, 6/5, 5/4, 4/3, 45/32, 3/2, 8/5, 5/3, 9/5, 15/8</item>
/// <item>PR.MN just minor (5-limit, Ptolemaic minor): 1, 16/15, 10/9, 6/5, 5/4, 4/3, 64/45, 3/2, 8/5, 5/3, 16/9, 15/8</item>
/// <item>PY.SH Pythagorean, chain of fifths F C G D A E B F# C# G# D# A# (3/2 steps folded into one octave); semitone 6 = F# = 729/512</item>
/// <item>PY.FL Pythagorean, chain C G D A E B / F Bb Eb Ab Db Gb; semitone 6 = Gb = 1024/729</item>
/// </list>
/// The pitch of note n is <c>(n - rel) + 12*log2(ratio[rel])</c> with rel = (n - key) mod 12, plus the master tune
/// (cents/100 semitones).
/// </para>
/// <para>
/// SLFG / CHKR / PLNT: the selected concert frequency is assigned to C4 (MIDI 60) and all other notes follow in 12-TET from it;
/// master tune is ignored; layers also ignore their OCTAVE param (<see cref="IsSpecial"/>, see the layers).
/// Out-of-range keys are clamped per temperament.
/// </para>
/// <para>
/// NOISE layer (<c>semitoneOnly</c>): PR.MJ..PY.FL and EQUA keep plain 12-TET semitone steps relative to the sample root (C4),
/// master tune is not applied; SLFG/CHKR/PLNT shift the sample by the ratio concertHz / 261.6256 (C4 in 12-TET) with
/// 12-TET semitone steps from there.
/// </para>
/// Audio-thread safe: <see cref="Update"/> only writes into the preallocated array and only when an input changed.
/// </summary>
public sealed class Tuning
{
    public static readonly double[] JustMajor = { 1, 16.0 / 15, 9.0 / 8, 6.0 / 5, 5.0 / 4, 4.0 / 3, 45.0 / 32, 3.0 / 2, 8.0 / 5, 5.0 / 3, 9.0 / 5, 15.0 / 8 };
    public static readonly double[] JustMinor = { 1, 16.0 / 15, 10.0 / 9, 6.0 / 5, 5.0 / 4, 4.0 / 3, 64.0 / 45, 3.0 / 2, 8.0 / 5, 5.0 / 3, 16.0 / 9, 15.0 / 8 };
    /// <summary>Fifths from the tonic for each semitone above it: sharp spelling.</summary>
    private static readonly int[] FifthsSharp = { 0, 7, 2, 9, 4, -1, 6, 1, 8, 3, 10, 5 };
    /// <summary>Fifths from the tonic for each semitone above it: flat spelling.</summary>
    private static readonly int[] FifthsFlat = { 0, -5, 2, -3, 4, -1, -6, 1, -4, 3, -2, 5 };
    public static readonly double[] PythagoreanSharp = BuildPythagorean(FifthsSharp);
    public static readonly double[] PythagoreanFlat = BuildPythagorean(FifthsFlat);

    public static readonly double[] SolfeggioHz = { 174, 285, 396, 417, 528, 639, 741, 852, 963 };
    public static readonly double[] ChakraC4Hz = { 256, 288, 320, 341, 384, 426, 480 };
    public static readonly double[] PlanetaryC4Hz = { 194.18, 210.42, 234.16, 270, 282.4, 315.4, 352 };

    /// <summary>C4 (MIDI 60) in 12-TET A4=440.</summary>
    public const double C4Hz12Tet = 261.6255653005986;

    private readonly float[] _midi = new float[128];
    private readonly bool _semitoneOnly;
    private Temperament _t = (Temperament)(-1);
    private int _key = -1;
    private float _master = float.NaN;

    public Tuning(bool semitoneOnly = false)
    {
        _semitoneOnly = semitoneOnly;
        for (int i = 0; i < 128; i++) _midi[i] = i;
    }

    /// <summary>Tuned pitch (fractional MIDI note, 12-TET A4=440 grid) of <paramref name="note"/> (clamped to 0..127).</summary>
    public float MidiFor(int note) => _midi[note < 0 ? 0 : note > 127 ? 127 : note];

    /// <summary>Tuned frequency of <paramref name="note"/> in Hz.</summary>
    public double HzFor(int note) => 440.0 * Math.Pow(2.0, (MidiFor(note) - 69.0) / 12.0);

    public Temperament Temperament => _t;
    /// <summary>SLFG / CHKR / PLNT: master tune and the layer OCTAVE param are ignored.</summary>
    public bool IsSpecial => IsSpecialTemperament(_t);

    public static bool IsSpecialTemperament(Temperament t) => t >= Temperament.Solfeggio;

    /// <summary>Number of selectable keys for a temperament (12 tonics, or 9 / 7 / 7 concert pitches).</summary>
    public static int KeyCount(Temperament t) => t switch
    {
        Temperament.Solfeggio => SolfeggioHz.Length,
        Temperament.Chakra => ChakraC4Hz.Length,
        Temperament.Planetary => PlanetaryC4Hz.Length,
        _ => 12,
    };

    /// <summary>Recomputes the table when an input changed (cheap compare otherwise). Allocation-free.</summary>
    public void Update(Temperament t, int key, float masterCents)
    {
        if ((uint)t > (uint)Temperament.Planetary) t = Temperament.Equal;
        key = Math.Clamp(key, 0, KeyCount(t) - 1);
        if (t == _t && key == _key && masterCents == _master) return;
        _t = t; _key = key; _master = masterCents;
        Fill(_midi, t, key, masterCents, _semitoneOnly);
    }

    /// <summary>Fills <paramref name="dest"/> (128 entries) with the fractional MIDI pitches.</summary>
    public static void Fill(float[] dest, Temperament t, int key, float masterCents, bool semitoneOnly = false)
    {
        key = Math.Clamp(key, 0, KeyCount(t) - 1);
        if (IsSpecialTemperament(t))
        {
            double c4 = t switch
            {
                Temperament.Solfeggio => SolfeggioHz[key],
                Temperament.Chakra => ChakraC4Hz[key],
                _ => PlanetaryC4Hz[key],
            };
            double c4Midi = 69.0 + 12.0 * Math.Log2(c4 / 440.0);
            for (int n = 0; n < 128; n++) dest[n] = (float)(c4Midi + (n - 60));
            return;
        }
        double master = semitoneOnly ? 0.0 : masterCents * 0.01;
        double[]? ratios = t switch
        {
            Temperament.PureMajor => JustMajor,
            Temperament.PureMinor => JustMinor,
            Temperament.PythagoreanSharp => PythagoreanSharp,
            Temperament.PythagoreanFlat => PythagoreanFlat,
            _ => null,
        };
        for (int n = 0; n < 128; n++)
        {
            if (ratios is null || semitoneOnly) { dest[n] = (float)(n + master); continue; }
            int rel = (((n - key) % 12) + 12) % 12;
            dest[n] = (float)((n - rel) + 12.0 * Math.Log2(ratios[rel]) + master);
        }
    }

    private static double[] BuildPythagorean(int[] fifths)
    {
        var r = new double[12];
        for (int i = 0; i < 12; i++)
        {
            double x = Math.Pow(1.5, fifths[i]);
            while (x >= 2.0) x *= 0.5;
            while (x < 1.0) x *= 2.0;
            r[i] = x;
        }
        return r;
    }
}
