using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sambient.App.Controls;

/// <summary>Small picture of an LFO / MOD waveform (<see cref="Shape"/> = LfoShape index); amplitude follows <see cref="Depth"/> (0..127).</summary>
public class LfoPreview : Avalonia.Controls.Control
{
    public static readonly StyledProperty<int> ShapeProperty = AvaloniaProperty.Register<LfoPreview, int>(nameof(Shape));
    public static readonly StyledProperty<double> DepthProperty = AvaloniaProperty.Register<LfoPreview, double>(nameof(Depth), 127);

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#14161b"));
    private static readonly Pen Mid = new(new SolidColorBrush(Color.Parse("#2a2e37")), 1);
    private static readonly Pen Line = new(new SolidColorBrush(Color.Parse("#5fb3ff")), 1.5, lineJoin: PenLineJoin.Round);

    static LfoPreview() => AffectsRender<LfoPreview>(ShapeProperty, DepthProperty);

    public int Shape { get => GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public double Depth { get => GetValue(DepthProperty); set => SetValue(DepthProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.DrawRectangle(Back, null, r, 4, 4);
        double mid = r.Height / 2;
        ctx.DrawLine(Mid, new Point(0, mid), new Point(r.Width, mid));
        double amp = (r.Height / 2 - 4) * (0.3 + 0.7 * Math.Clamp(Depth, 0, 127) / 127.0);
        int n = Math.Max(2, (int)r.Width);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / (n - 1) * LfoShapes.WindowCycles;
                var pt = new Point(i * r.Width / (n - 1), mid - LfoShapes.Eval(Shape, t) * amp);
                if (i == 0) g.BeginFigure(pt, false); else g.LineTo(pt);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, Line, geo);
    }
}
