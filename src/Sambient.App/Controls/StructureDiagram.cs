using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sambient.App.Controls;

/// <summary>
/// Tiny Yin/Yang diagram of a structure (<c>Sambient.Engine.Structure</c> index): filled circle = Yang oscillator, hollow = Yin.
/// DRN1 ●●, DRN2 ●○, PAD1 ●○○, PAD2 ●●○, ATM1 ● + "RING", ATM2 ●→● "FM". Index -1 (NOISE layer) draws nothing.
/// </summary>
public class StructureDiagram : Avalonia.Controls.Control
{
    public static readonly StyledProperty<int> StructureProperty = AvaloniaProperty.Register<StructureDiagram, int>(nameof(Structure), -1);

    private static readonly IBrush YangBrush = new SolidColorBrush(Color.Parse("#5fb3ff"));
    private static readonly Pen YinPen = new(new SolidColorBrush(Color.Parse("#5fb3ff")), 1.6);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#8b91a0"));

    // per structure: circles (true = Yang), and an optional tag
    private static readonly (bool[] Osc, string? Tag, bool Arrow)[] Layouts =
    [
        ([true, true], null, false),            // DRONE1
        ([true, false], null, false),           // DRONE2
        ([true, false, false], null, false),    // PAD1
        ([true, true, false], null, false),     // PAD2
        ([true], "RING", false),                // ATMOS1
        ([true, true], "FM", true),             // ATMOS2
    ];

    static StructureDiagram() => AffectsRender<StructureDiagram>(StructureProperty);

    public int Structure { get => GetValue(StructureProperty); set => SetValue(StructureProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(112, 24);

    public override void Render(DrawingContext ctx)
    {
        if ((uint)Structure >= Layouts.Length) return;
        var (osc, tag, arrow) = Layouts[Structure];
        const double r = 7, step = 26;
        double cy = Bounds.Height / 2, x = r + 1;
        for (int i = 0; i < osc.Length; i++)
        {
            var c = new Point(x, cy);
            if (osc[i]) ctx.DrawEllipse(YangBrush, null, c, r, r);
            else ctx.DrawEllipse(null, YinPen, c, r - 0.8, r - 0.8);
            if (arrow && i == 0)
            {
                var a = new Point(x + r + 2, cy);
                var b = new Point(x + step - r - 2, cy);
                ctx.DrawLine(YinPen, a, b);
                ctx.DrawLine(YinPen, b, new Point(b.X - 3, cy - 3));
                ctx.DrawLine(YinPen, b, new Point(b.X - 3, cy + 3));
            }
            x += step;
        }
        if (tag != null)
        {
            var ft = new FormattedText(tag, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11, TextBrush);
            ctx.DrawText(ft, new Point(x - step + r + 8, cy - ft.Height / 2));
        }
    }
}
