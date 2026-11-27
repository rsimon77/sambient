using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Sambient.App.Controls;

/// <summary>
/// Custom-drawn rotary knob. Vertical drag changes the value (Shift = fine), mouse wheel steps it, double-click resets to
/// <see cref="DefaultValue"/>. Right-click opens a menu with MIDI Learn / Clear MIDI mapping (executes
/// <see cref="LearnCommand"/> / <see cref="ClearMappingCommand"/> with <see cref="MidiTarget"/> as parameter).
/// <see cref="DisplayText"/> (e.g. "775 ms") is shown under the knob; when null the raw value is shown.
/// </summary>
public class Knob : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Knob, double>(nameof(Value), 0, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Knob, double>(nameof(Minimum), 0);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Knob, double>(nameof(Maximum), 127);
    public static readonly StyledProperty<double> DefaultValueProperty = AvaloniaProperty.Register<Knob, double>(nameof(DefaultValue), 0);
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<Knob, string?>(nameof(Label));
    public static readonly StyledProperty<string?> DisplayTextProperty = AvaloniaProperty.Register<Knob, string?>(nameof(DisplayText));
    public static readonly StyledProperty<bool> IsBipolarProperty = AvaloniaProperty.Register<Knob, bool>(nameof(IsBipolar));
    public static readonly StyledProperty<bool> IsLearningProperty = AvaloniaProperty.Register<Knob, bool>(nameof(IsLearning));
    public static readonly StyledProperty<string?> MidiTargetProperty = AvaloniaProperty.Register<Knob, string?>(nameof(MidiTarget));
    public static readonly StyledProperty<ICommand?> LearnCommandProperty = AvaloniaProperty.Register<Knob, ICommand?>(nameof(LearnCommand));
    public static readonly StyledProperty<ICommand?> ClearMappingCommandProperty = AvaloniaProperty.Register<Knob, ICommand?>(nameof(ClearMappingCommand));

    private static readonly IBrush BodyBrush = new SolidColorBrush(Color.Parse("#2b2f38"));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#3a3f4b"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#5fb3ff"));
    private static readonly IBrush LearnBrush = new SolidColorBrush(Color.Parse("#ffb454"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#e6e8ee"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#8b91a0"));
    private static readonly Typeface UiFont = new("Inter");

    private const double StartAngle = 135, SweepAngle = 270;
    private bool _dragging;
    private double _lastY;

    static Knob()
    {
        AffectsRender<Knob>(ValueProperty, MinimumProperty, MaximumProperty, LabelProperty, DisplayTextProperty,
            IsBipolarProperty, IsLearningProperty);
        FocusableProperty.OverrideDefaultValue<Knob>(true);
    }

    public Knob()
    {
        var learn = new MenuItem { Header = "MIDI Learn" };
        learn.Click += (_, _) => Execute(LearnCommand);
        var clear = new MenuItem { Header = "Clear MIDI mapping" };
        clear.Click += (_, _) => Execute(ClearMappingCommand);
        ContextMenu = new ContextMenu { ItemsSource = new[] { learn, clear } };
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double DefaultValue { get => GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? DisplayText { get => GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }
    public bool IsBipolar { get => GetValue(IsBipolarProperty); set => SetValue(IsBipolarProperty, value); }
    public bool IsLearning { get => GetValue(IsLearningProperty); set => SetValue(IsLearningProperty, value); }
    public string? MidiTarget { get => GetValue(MidiTargetProperty); set => SetValue(MidiTargetProperty, value); }
    public ICommand? LearnCommand { get => GetValue(LearnCommandProperty); set => SetValue(LearnCommandProperty, value); }
    public ICommand? ClearMappingCommand { get => GetValue(ClearMappingCommandProperty); set => SetValue(ClearMappingCommandProperty, value); }

    private void Execute(ICommand? c)
    {
        if (c != null && c.CanExecute(MidiTarget)) c.Execute(MidiTarget);
    }

    private double Range => Math.Max(1e-9, Maximum - Minimum);
    private void SetClamped(double v) => Value = Math.Clamp(v, Minimum, Maximum);

    protected override Size MeasureOverride(Size availableSize) => new(76, 104);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { SetClamped(DefaultValue); e.Handled = true; return; }
        _dragging = true;
        _lastY = p.Position.Y;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        double y = e.GetPosition(this).Y;
        double dy = _lastY - y;
        _lastY = y;
        double perPixel = Range / 200.0;                 // 200 px of travel = full range
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) perPixel *= 0.1;
        SetClamped(Value + dy * perPixel);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging) { _dragging = false; e.Pointer.Capture(null); e.Handled = true; }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double step = Range / 100.0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step *= 0.1;
        SetClamped(Value + Math.Sign(e.Delta.Y) * step);
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        var c = new Point(w / 2, 38);
        const double r = 26;
        double t = Math.Clamp((Value - Minimum) / Range, 0, 1);

        ctx.DrawEllipse(BodyBrush, null, c, r - 6, r - 6);
        DrawArc(ctx, c, r, 0, 1, new Pen(TrackBrush, 4, lineCap: PenLineCap.Round));
        var active = IsLearning ? LearnBrush : AccentBrush;
        var pen = new Pen(active, 4, lineCap: PenLineCap.Round);
        if (IsBipolar) DrawArc(ctx, c, r, Math.Min(0.5, t), Math.Max(0.5, t), pen);
        else DrawArc(ctx, c, r, 0, t, pen);

        // pointer line
        double a = (StartAngle + SweepAngle * t) * Math.PI / 180.0;
        var p1 = new Point(c.X + Math.Cos(a) * (r - 18), c.Y + Math.Sin(a) * (r - 18));
        var p2 = new Point(c.X + Math.Cos(a) * (r - 9), c.Y + Math.Sin(a) * (r - 9));
        ctx.DrawLine(new Pen(TextBrush, 2, lineCap: PenLineCap.Round), p1, p2);

        string value = IsLearning ? "learn…" : DisplayText ?? Value.ToString("0.#", CultureInfo.InvariantCulture);
        DrawText(ctx, value, 12, IsLearning ? LearnBrush : TextBrush, w, 72);
        if (!string.IsNullOrEmpty(Label)) DrawText(ctx, Label!, 11, DimBrush, w, 88);
    }

    private static void DrawArc(DrawingContext ctx, Point c, double r, double t0, double t1, Pen pen)
    {
        if (t1 - t0 < 0.002) return;
        double a0 = (StartAngle + SweepAngle * t0) * Math.PI / 180.0;
        double a1 = (StartAngle + SweepAngle * t1) * Math.PI / 180.0;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r), false);
            g.ArcTo(new Point(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r), new Size(r, r), 0,
                (t1 - t0) * SweepAngle > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private static void DrawText(DrawingContext ctx, string text, double size, IBrush brush, double width, double y)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiFont, size, brush);
        ctx.DrawText(ft, new Point((width - ft.Width) / 2, y));
    }
}
