using Avalonia.Input;

namespace Sambient.App.Controls;

/// <summary>
/// Plays notes from the computer keyboard (piano layout on the home row, black keys on the row above):
/// <code>
///   W E   T Y U   O P        (C# D#  F# G# A#  C# D#)    Y and Z both mean G# so QWERTY and QWERTZ layouts work
///  A S D F G H J K L         (C  D  E  F  G  A  B  C  D)
/// </code>
/// Octave down / up: , (comma) and . (period) — Z/X are not used because Y/Z are note keys. Auto-repeat is ignored;
/// key-up releases the note that key started (even if the octave changed meanwhile). Keys with Ctrl/Cmd/Alt are ignored.
/// </summary>
public sealed class ComputerKeyboard
{
    private static readonly Dictionary<Key, int> Semitones = new()
    {
        [Key.A] = 0, [Key.W] = 1, [Key.S] = 2, [Key.E] = 3, [Key.D] = 4, [Key.F] = 5, [Key.T] = 6, [Key.G] = 7,
        [Key.Y] = 8, [Key.Z] = 8, [Key.H] = 9, [Key.U] = 10, [Key.J] = 11, [Key.K] = 12, [Key.O] = 13, [Key.L] = 14, [Key.P] = 15,
    };

    private readonly Func<int> _firstNote;
    private readonly Action<int> _noteOn;
    private readonly Action<int> _noteOff;
    private readonly Action<int> _shiftOctave;
    private readonly Dictionary<Key, int> _down = new();

    /// <param name="firstNote">Current MIDI note of the C that the A key plays.</param>
    /// <param name="noteOn">Called with the note; velocity is the layer's fixed VELO parameter (velocity 0 in the engine).</param>
    public ComputerKeyboard(Func<int> firstNote, Action<int> noteOn, Action<int> noteOff, Action<int> shiftOctave)
    {
        _firstNote = firstNote; _noteOn = noteOn; _noteOff = noteOff; _shiftOctave = shiftOctave;
    }

    /// <summary>Returns true if the key was consumed.</summary>
    public bool KeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        if (e.Key == Key.OemComma || e.Key == Key.OemPeriod)
        {
            if (!_down.ContainsKey(e.Key))
            {
                _down[e.Key] = -1;
                _shiftOctave(e.Key == Key.OemComma ? -1 : 1);
            }
            return true;
        }
        if (!Semitones.TryGetValue(e.Key, out int semi)) return false;
        if (_down.ContainsKey(e.Key)) return true;        // auto-repeat
        int note = _firstNote() + semi;
        if (note > 127) return true;
        _down[e.Key] = note;
        _noteOn(note);
        return true;
    }

    public bool KeyUp(KeyEventArgs e)
    {
        if (!_down.Remove(e.Key, out int note)) return false;
        if (note >= 0) _noteOff(note);
        return true;
    }

    /// <summary>Releases every note this keyboard is holding (focus loss, window close, panel hidden).</summary>
    public void ReleaseAll()
    {
        foreach (var note in _down.Values)
            if (note >= 0) _noteOff(note);
        _down.Clear();
    }
}
