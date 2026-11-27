using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sambient.App.ViewModels;
using Sambient.Engine;

namespace Sambient.App.Controls;

/// <summary>
/// Custom-drawn piano: <see cref="Octaves"/> octaves (default 3) plus the closing C, starting at the C of
/// <see cref="BaseOctave"/> (MIDI note = (octave + 1) * 12, so C3 = 48). Mouse: press = NoteOn, release = NoteOff, drag across
/// keys = glissando (pointer is captured so a release outside the control still sends NoteOff). Velocity comes from the
/// vertical click position on the key (top ~40, bottom 127). Highlights notes reported by <see cref="State"/>.
/// </summary>
public class PianoKeyboard : Avalonia.Controls.Control
{
    public static readonly StyledProperty<int> BaseOctaveProperty = AvaloniaProperty.Register<PianoKeyboard, int>(nameof(BaseOctave), 3);
    public static readonly StyledProperty<int> OctavesProperty = AvaloniaProperty.Register<PianoKeyboard, int>(nameof(Octaves), 3);
    public static readonly StyledProperty<HeldNoteTracker?> StateProperty = AvaloniaProperty.Register<PianoKeyboard, HeldNoteTracker?>(nameof(State));

    public static readonly StyledProperty<LatchedNoteSet?> LatchedProperty = AvaloniaProperty.Register<PianoKeyboard, LatchedNoteSet?>(nameof(Latched));
    public static readonly StyledProperty<bool> HoldModeProperty = AvaloniaProperty.Register<PianoKeyboard, bool>(nameof(HoldMode));

    private const int MinVelocity = 40, MaxVelocity = 127;
    private static readonly int[] WhiteOffsets = [0, 2, 4, 5, 7, 9, 11];
    private static readonly bool[] BlackAfterDegree = [true, true, false, true, true, true, false];

    private static readonly IBrush WhiteBrush = new SolidColorBrush(Color.Parse("#e8eaf0"));
    private static readonly IBrush BlackBrush = new SolidColorBrush(Color.Parse("#16181d"));
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#5fb3ff"));
    private static readonly IBrush ActiveBlackBrush = new SolidColorBrush(Color.Parse("#3a86d6"));
    private static readonly Pen EdgePen = new(new SolidColorBrush(Color.Parse("#0e1013")), 1);
    private static readonly IBrush LatchedBrush = new SolidColorBrush(Color.Parse("#ffb454"));
    private static readonly IBrush LatchedBlackBrush = new SolidColorBrush(Color.Parse("#d9902f"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6b7180"));

    private int _mouseNote = -1;
    private HeldNoteTracker? _subscribed;
    private LatchedNoteSet? _subscribedLatched;

    static PianoKeyboard() => AffectsRender<PianoKeyboard>(BaseOctaveProperty, OctavesProperty);

    public int BaseOctave { get => GetValue(BaseOctaveProperty); set => SetValue(BaseOctaveProperty, value); }
    public int Octaves { get => GetValue(OctavesProperty); set => SetValue(OctavesProperty, value); }
    /// <summary>HOLD-latched notes of the displayed layer, drawn in a distinct colour.</summary>
    public LatchedNoteSet? Latched { get => GetValue(LatchedProperty); set => SetValue(LatchedProperty, value); }
    /// <summary>True while HOLD is on: a click only toggles the key (no glissando across keys while the button is down).</summary>
    public bool HoldMode { get => GetValue(HoldModeProperty); set => SetValue(HoldModeProperty, value); }
    public HeldNoteTracker? State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>(note, velocity)</summary>
    public event Action<int, int>? NoteOn;
    public event Action<int>? NoteOff;

    public int FirstNote => (BaseOctave + 1) * 12;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty)
        {
            if (_subscribed != null) _subscribed.Changed -= OnStateChanged;
            _subscribed = change.NewValue as HeldNoteTracker;
            if (_subscribed != null) _subscribed.Changed += OnStateChanged;
            InvalidateVisual();
        }
        else if (change.Property == LatchedProperty)
        {
            if (_subscribedLatched != null) _subscribedLatched.Changed -= OnStateChanged;
            _subscribedLatched = change.NewValue as LatchedNoteSet;
            if (_subscribedLatched != null) _subscribedLatched.Changed += OnStateChanged;
            InvalidateVisual();
        }
        else if (change.Property == BaseOctaveProperty)
        {
            ReleaseAll();    // the key under the mouse would otherwise change its pitch
        }
    }

    private void OnStateChanged() => InvalidateVisual();

    /// <summary>Releases the note currently held with the mouse (if any).</summary>
    public void ReleaseAll()
    {
        if (_mouseNote >= 0)
        {
            int n = _mouseNote;
            _mouseNote = -1;
            NoteOff?.Invoke(n);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseAll();
        base.OnDetachedFromVisualTree(e);
    }

    // ------------------------------------------------------------------ geometry / hit testing

    private int WhiteCount => Octaves * 7 + 1;
    private double WhiteWidth => Bounds.Width / WhiteCount;
    private double BlackHeight => Bounds.Height * 0.62;
    private double BlackWidth => WhiteWidth * 0.6;

    private int WhiteNote(int whiteIndex) => FirstNote + (whiteIndex / 7) * 12 + WhiteOffsets[whiteIndex % 7];

    /// <summary>Returns the note under a point and the 0..1 vertical position within that key; -1 if none.</summary>
    private int HitTest(Point p, out double yFraction)
    {
        yFraction = 0;
        double ww = WhiteWidth, bw = BlackWidth, bh = BlackHeight;
        double x = Math.Clamp(p.X, 0, Bounds.Width - 0.001);
        double y = Math.Clamp(p.Y, 0, Bounds.Height);
        int wi = Math.Min((int)(x / ww), WhiteCount - 1);
        // black keys sit on the boundary after white key wi (right side) and before it (left side)
        if (y < bh)
        {
            for (int i = Math.Max(0, wi - 1); i <= Math.Min(WhiteCount - 2, wi); i++)
            {
                if (!BlackAfterDegree[i % 7]) continue;
                double cx = (i + 1) * ww;
                if (Math.Abs(x - cx) <= bw / 2) { yFraction = y / bh; return WhiteNote(i) + 1; }
            }
        }
        yFraction = y / Bounds.Height;
        return WhiteNote(wi);
    }

    private static int VelocityFrom(double yFraction)
        => (int)Math.Round(MinVelocity + (MaxVelocity - MinVelocity) * Math.Clamp(yFraction, 0, 1));

    // ------------------------------------------------------------------ mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _mouseNote >= 0) return;
        e.Pointer.Capture(this);
        int note = HitTest(e.GetPosition(this), out double yf);
        _mouseNote = note;
        NoteOn?.Invoke(note, VelocityFrom(yf));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_mouseNote < 0 || HoldMode) return;
        int note = HitTest(e.GetPosition(this), out double yf);
        if (note == _mouseNote) return;
        int old = _mouseNote;
        _mouseNote = note;
        NoteOff?.Invoke(old);
        NoteOn?.Invoke(note, VelocityFrom(yf));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mouseNote < 0) return;
        e.Pointer.Capture(null);
        ReleaseAll();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ReleaseAll();
    }

    // ------------------------------------------------------------------ drawing

    public override void Render(DrawingContext ctx)
    {
        double ww = WhiteWidth, h = Bounds.Height, bw = BlackWidth, bh = BlackHeight;
        var state = State;
        var latched = Latched;
        int count = WhiteCount;

        for (int i = 0; i < count; i++)
        {
            int note = WhiteNote(i);
            var rect = new Rect(i * ww, 0, ww, h);
            ctx.DrawRectangle(latched?.IsLatched(note) == true ? LatchedBrush
                : state?.IsSounding(note) == true ? ActiveBrush : WhiteBrush, EdgePen, rect);
            if (note % 12 == 0)
            {
                var ft = new FormattedText("C" + (note / 12 - 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Inter"), 11, LabelBrush);
                ctx.DrawText(ft, new Point(rect.X + (ww - ft.Width) / 2, h - ft.Height - 5));
            }
        }
        for (int i = 0; i < count - 1; i++)
        {
            if (!BlackAfterDegree[i % 7]) continue;
            int note = WhiteNote(i) + 1;
            var rect = new Rect((i + 1) * ww - bw / 2, 0, bw, bh);
            ctx.DrawRectangle(latched?.IsLatched(note) == true ? LatchedBlackBrush
                : state?.IsSounding(note) == true ? ActiveBlackBrush : BlackBrush, EdgePen, rect, 0, 0);
        }
    }
}
