using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sambient.Dsp.Wavetables;
using System.Globalization;

namespace Sambient.App.Controls;

/// <summary>Draws one cycle of a wavetable (mip level 3) at a HARMONIC position; "building…" while the wave is not ready.</summary>
public class WavePreview : Avalonia.Controls.Control
{
    public static readonly StyledProperty<Wavetable?> TableProperty = AvaloniaProperty.Register<WavePreview, Wavetable?>(nameof(Table));
    public static readonly StyledProperty<double> HarmonicProperty = AvaloniaProperty.Register<WavePreview, double>(nameof(Harmonic));
    public static readonly StyledProperty<bool> IsBuildingProperty = AvaloniaProperty.Register<WavePreview, bool>(nameof(IsBuilding));

    private const int PreviewLevel = 3;
    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#14161b"));
    private static readonly Pen Grid = new(new SolidColorBrush(Color.Parse("#2a2e37")), 1);
    private static readonly Pen Line = new(new SolidColorBrush(Color.Parse("#5fb3ff")), 1.6, lineJoin: PenLineJoin.Round);
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8b91a0"));

    static WavePreview() => AffectsRender<WavePreview>(TableProperty, HarmonicProperty, IsBuildingProperty);

    public Wavetable? Table { get => GetValue(TableProperty); set => SetValue(TableProperty, value); }
    public double Harmonic { get => GetValue(HarmonicProperty); set => SetValue(HarmonicProperty, value); }
    public bool IsBuilding { get => GetValue(IsBuildingProperty); set => SetValue(IsBuildingProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.DrawRectangle(Back, null, new RoundedRect(rect, 6));
        double mid = rect.Height / 2;
        ctx.DrawLine(Grid, new Point(0, mid), new Point(rect.Width, mid));

        var table = Table;
        if (table == null || IsBuilding)
        {
            var ft = new FormattedText(IsBuilding ? "building…" : "no wave", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Inter"), 12, Dim);
            ctx.DrawText(ft, new Point((rect.Width - ft.Width) / 2, mid - ft.Height / 2));
            if (table == null) return;
        }

        double h = Math.Clamp(Harmonic, 0, Wavetable.FrameCount - 1);
        int f0 = (int)h, f1 = Math.Min(f0 + 1, Wavetable.FrameCount - 1);
        float frac = (float)(h - f0);
        var a = table.GetCycle(f0, PreviewLevel);
        var b = table.GetCycle(f1, PreviewLevel);
        int n = a.Length;
        int points = Math.Max(2, (int)rect.Width);
        double amp = mid * 0.8;       // headroom: darker mips can peak above 1
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < points; i++)
            {
                double pos = (double)i / (points - 1) * (n - 1);
                int k = (int)pos;
                int k2 = Math.Min(k + 1, n - 1);
                float fk = (float)(pos - k);
                float ya = a[k] + (a[k2] - a[k]) * fk;
                float yb = b[k] + (b[k2] - b[k]) * fk;
                double y = mid - (ya + (yb - ya) * frac) * amp;
                y = Math.Clamp(y, 1, rect.Height - 1);
                var p = new Point(i * rect.Width / (points - 1), y);
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        using (ctx.PushOpacity(IsBuilding ? 0.35 : 1.0)) ctx.DrawGeometry(null, Line, geo);
    }
}
