using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sambient.Dsp.Noise;
using Sambient.Dsp.Wavetables;
using System.Globalization;

namespace Sambient.App.Controls;

/// <summary>With <see cref="Sample"/> set (NOISE layer) it draws the sample's waveform overview instead.
/// Draws one cycle of a wavetable (mip level 3) at a HARMONIC position; "building…" while the wave is not ready.</summary>
public class WavePreview : Avalonia.Controls.Control
{
    public static readonly StyledProperty<Wavetable?> TableProperty = AvaloniaProperty.Register<WavePreview, Wavetable?>(nameof(Table));
    public static readonly StyledProperty<double> HarmonicProperty = AvaloniaProperty.Register<WavePreview, double>(nameof(Harmonic));
    public static readonly StyledProperty<bool> IsBuildingProperty = AvaloniaProperty.Register<WavePreview, bool>(nameof(IsBuilding));

    public static readonly StyledProperty<NoiseSample?> SampleProperty = AvaloniaProperty.Register<WavePreview, NoiseSample?>(nameof(Sample));
    public static readonly StyledProperty<bool> IsSampleModeProperty = AvaloniaProperty.Register<WavePreview, bool>(nameof(IsSampleMode));

    private const int PreviewLevel = 3;
    private const int OverviewBuckets = 400;
    private float[]? _peaks;           // min/max pairs per bucket of the current sample
    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#14161b"));
    private static readonly Pen Grid = new(new SolidColorBrush(Color.Parse("#2a2e37")), 1);
    private static readonly Pen Line = new(new SolidColorBrush(Color.Parse("#5fb3ff")), 1.6, lineJoin: PenLineJoin.Round);
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8b91a0"));

    static WavePreview() => AffectsRender<WavePreview>(TableProperty, HarmonicProperty, IsBuildingProperty, SampleProperty, IsSampleModeProperty);

    public Wavetable? Table { get => GetValue(TableProperty); set => SetValue(TableProperty, value); }
    public double Harmonic { get => GetValue(HarmonicProperty); set => SetValue(HarmonicProperty, value); }
    public bool IsBuilding { get => GetValue(IsBuildingProperty); set => SetValue(IsBuildingProperty, value); }

    public NoiseSample? Sample { get => GetValue(SampleProperty); set => SetValue(SampleProperty, value); }
    /// <summary>True on the NOISE layer: draw <see cref="Sample"/> (or "building…") instead of a wavetable cycle.</summary>
    public bool IsSampleMode { get => GetValue(IsSampleModeProperty); set => SetValue(IsSampleModeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SampleProperty) _peaks = ComputePeaks(change.GetNewValue<NoiseSample?>());
    }

    private static float[]? ComputePeaks(NoiseSample? s)
    {
        if (s == null || s.Length == 0) return null;
        var peaks = new float[OverviewBuckets * 2];
        for (int b = 0; b < OverviewBuckets; b++)
        {
            int from = (int)((long)b * s.Length / OverviewBuckets), to = Math.Max(from + 1, (int)((long)(b + 1) * s.Length / OverviewBuckets));
            float lo = 0, hi = 0;
            for (int i = from; i < to && i < s.Length; i++)
            {
                float v = s.Right.Length == s.Left.Length ? 0.5f * (s.Left[i] + s.Right[i]) : s.Left[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            peaks[2 * b] = lo; peaks[2 * b + 1] = hi;
        }
        return peaks;
    }

    private void RenderOverview(DrawingContext ctx, Rect rect, double mid)
    {
        var peaks = _peaks;
        if (peaks == null || IsBuilding)
        {
            var ft = new FormattedText("building…", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 12, Dim);
            ctx.DrawText(ft, new Point((rect.Width - ft.Width) / 2, mid - ft.Height / 2));
            return;
        }
        float max = 0.05f;
        for (int i = 0; i < peaks.Length; i++) max = Math.Max(max, Math.Abs(peaks[i]));
        double amp = mid * 0.9 / max;
        for (int b = 0; b < OverviewBuckets; b++)
        {
            double x = (b + 0.5) * rect.Width / OverviewBuckets;
            double y0 = mid - peaks[2 * b + 1] * amp, y1 = mid - peaks[2 * b] * amp;
            if (y1 - y0 < 1) y1 = y0 + 1;
            ctx.DrawLine(Line, new Point(x, y0), new Point(x, y1));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.DrawRectangle(Back, null, new RoundedRect(rect, 6));
        double mid = rect.Height / 2;
        ctx.DrawLine(Grid, new Point(0, mid), new Point(rect.Width, mid));

        if (IsSampleMode) { RenderOverview(ctx, rect, mid); return; }

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
