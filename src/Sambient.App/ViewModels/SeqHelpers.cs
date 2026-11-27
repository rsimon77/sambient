using System.Globalization;
using System.Text;
using Sambient.Engine;
using Sambient.Seq;

namespace Sambient.App.ViewModels;

/// <summary>How a step cell takes part in a tie (a note sounding over several steps).</summary>
public enum TiePart { None, Start, Mid, End }

/// <summary>Everything a step button shows, derived from a <see cref="StepSnapshot"/> (pure, unit-testable).</summary>
public sealed record StepCellInfo(int Index, bool InLength, bool HasNotes, int NoteCount, bool HasLocks, int Probability, TiePart Tie)
{
    /// <summary>"" for 100 %, otherwise e.g. "50%".</summary>
    public string ProbabilityText => Probability < 100 ? Probability.ToString(CultureInfo.InvariantCulture) + "%" : "";
    public bool TieFromPrevious => Tie is TiePart.Mid or TiePart.End;
    public bool TieToNext => Tie is TiePart.Start or TiePart.Mid;
}

/// <summary>Step-grid logic: pages of 16 steps, page enabling by LENGTH and the per-cell state.</summary>
public static class StepGrid
{
    public const int StepsPerPage = 16;
    public const int PageCount = SeqLimits.MaxSteps / StepsPerPage;

    /// <summary>Pages 1..4 are 1-16 / 17-32 / 33-48 / 49-64.</summary>
    public static string PageLabel(int page) => $"{page * StepsPerPage + 1}–{(page + 1) * StepsPerPage}";

    /// <summary>A page is enabled when its first step lies within the track length.</summary>
    public static bool IsPageEnabled(int page, int length) => (uint)page < PageCount && page * StepsPerPage < length;

    /// <summary>Number of the last enabled page (0-based) for a track length.</summary>
    public static int LastPage(int length) => Math.Clamp((Math.Clamp(length, 1, SeqLimits.MaxSteps) - 1) / StepsPerPage, 0, PageCount - 1);

    /// <summary>
    /// Derives the 16 cells of a page. Ties cover the steps after the note's step up to its tie length (clamped to the track
    /// length, no wrap); a cell inside a tie shows a continuation bar, the last one an end bar.
    /// </summary>
    public static StepCellInfo[] Derive(TrackSnapshot track, int page)
    {
        int len = Math.Clamp(track.Length, 1, SeqLimits.MaxSteps);
        var starts = new bool[SeqLimits.MaxSteps];
        var covered = new bool[SeqLimits.MaxSteps];
        var ends = new bool[SeqLimits.MaxSteps];
        for (int s = 0; s < len && s < track.Steps.Count; s++)
        {
            foreach (var n in track.Steps[s].Notes)
            {
                int end = Math.Min(s + n.Tie - 1, len - 1);
                if (end <= s) continue;
                starts[s] = true;
                for (int i = s + 1; i <= end; i++) covered[i] = true;
                ends[end] = true;
            }
        }
        var cells = new StepCellInfo[StepsPerPage];
        for (int c = 0; c < StepsPerPage; c++)
        {
            int idx = page * StepsPerPage + c;
            if (idx >= SeqLimits.MaxSteps || idx >= track.Steps.Count)
            {
                cells[c] = new StepCellInfo(Math.Min(idx, SeqLimits.MaxSteps - 1), false, false, 0, false, 100, TiePart.None);
                continue;
            }
            var st = track.Steps[idx];
            TiePart tie = TiePart.None;
            if (covered[idx]) tie = starts[idx] ? TiePart.Mid : (ends[idx] ? TiePart.End : TiePart.Mid);
            else if (starts[idx]) tie = TiePart.Start;
            cells[c] = new StepCellInfo(idx, idx < len, st.HasNotes, st.Notes.Count, st.HasLocks, st.Probability, tie);
        }
        return cells;
    }
}

/// <summary>MIDI note names (C4 = 60).</summary>
public static class NoteNames
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    public static string Name(int note) => $"{Names[((note % 12) + 12) % 12]}{Math.DivRem(note, 12).Quotient - 1}";
}

/// <summary>Pattern-palette name editing: uppercase A-Z, 0-9, "." and "-", at most 8 characters.</summary>
public static class PatternNames
{
    public static string Sanitize(string? text)
    {
        var sb = new StringBuilder();
        foreach (char ch in (text ?? "").ToUpperInvariant())
        {
            if ((ch is >= 'A' and <= 'Z') || (ch is >= '0' and <= '9') || ch is '.' or '-') sb.Append(ch);
            if (sb.Length >= PatternPalette.NameMax) break;
        }
        return sb.ToString();
    }
}

/// <summary>Temperament names, key labels and cent / Hz conversion of the tuning panel.</summary>
public static class TuningLabels
{
    public static readonly string[] TemperamentNames = ["EQUA", "PR.MJ", "PR.MN", "PY.SH", "PY.FL", "SLFG", "CHKR", "PLNT"];

    private static readonly string[] Sharp = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] Flat = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];
    private static readonly string[] Ordinals = ["1st", "2nd", "3rd", "4th", "5th", "6th", "7th"];

    /// <summary>Key (tonic / concert pitch) labels; the count always equals <see cref="Tuning.KeyCount"/>.</summary>
    public static IReadOnlyList<string> KeyLabels(Temperament t) => t switch
    {
        Temperament.Solfeggio => Tuning.SolfeggioHz.Select(h => h.ToString("0", CultureInfo.InvariantCulture)).ToArray(),
        Temperament.Chakra or Temperament.Planetary => Ordinals,
        Temperament.PythagoreanFlat => Flat,
        _ => Sharp,
    };

    /// <summary>EQUA has no key: the combo is disabled.</summary>
    public static bool KeyEnabled(Temperament t) => t != Temperament.Equal;

    /// <summary>Frequency of A4 for a master tune in cents: <c>440 * 2^(c/1200)</c>.</summary>
    public static double CentsToHz(double cents) => 440.0 * Math.Pow(2.0, cents / 1200.0);

    public static double HzToCents(double hz) => 1200.0 * Math.Log2(hz / 440.0);

    public static string FormatCents(double cents)
    {
        double r = Math.Round(cents, MidpointRounding.AwayFromZero);
        return (r == 0 ? "+0" : r.ToString("+0;-0", CultureInfo.InvariantCulture)) + " ct";
    }

    public static string FormatMaster(double cents, bool showHz)
        => showHz ? CentsToHz(cents).ToString("0.0", CultureInfo.InvariantCulture) + " Hz" : FormatCents(cents);
}

/// <summary>Display names of layer parameters (locks list).</summary>
public static class ParamLabels
{
    /// <summary>"Lfo1Rate" -> "LFO1 RATE".</summary>
    public static string Of(ParamId id)
    {
        var s = id.ToString();
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(s[i]));
        }
        return sb.ToString();
    }

    public static string FormatValue(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Keyboard shortcut help text (also used as tooltip).</summary>
public static class ShortcutHelp
{
    public const string Text =
        "Space  play / stop\nShift+Space  stop with natural release\nR  record on / off\nM  metronome\nN  PRM REC\n" +
        "← →  move the step cursor\nDelete / Backspace  clear the selected step\n1 – 4  select layer\n" +
        "(piano keys A W S E D F T G Y Z H U J K O L P and , . are never used as shortcuts)";
}
