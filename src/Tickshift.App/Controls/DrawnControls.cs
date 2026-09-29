using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Tickshift.Core.Models;

namespace Tickshift.App.Controls;

/// <summary>Circular gauge drawn in OnRender — no animation, no bitmap effects, one draw call per update.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(nameof(ValueBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(5d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush ValueBrush { get => (Brush)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var radius = (size - Thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 100);
        if (value <= 0) return;
        var pen = new Pen(ValueBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 99.9)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = value / 100 * 360;
        var start = PointOn(center, radius, 0);
        var end = PointOn(center, radius, angle);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOn(Point c, double r, double degrees)
    {
        var rad = (degrees - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}

/// <summary>
/// Lightweight time-series chart for live metrics. Draws only when its data changes (callers hand it a
/// new, already-downsampled list), so idle cost is zero. Supports CPU, memory, disk and temperature series.
/// </summary>
public sealed class PerformanceChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = Dp(nameof(Samples), typeof(IReadOnlyList<MetricSample>), null);
    public static readonly DependencyProperty ShowCpuProperty = Dp(nameof(ShowCpu), typeof(bool), true);
    public static readonly DependencyProperty ShowMemoryProperty = Dp(nameof(ShowMemory), typeof(bool), true);
    public static readonly DependencyProperty ShowDiskProperty = Dp(nameof(ShowDisk), typeof(bool), false);
    public static readonly DependencyProperty ShowTemperatureProperty = Dp(nameof(ShowTemperature), typeof(bool), false);
    public static readonly DependencyProperty CpuBrushProperty = Dp(nameof(CpuBrush), typeof(Brush), Brushes.Teal);
    public static readonly DependencyProperty MemoryBrushProperty = Dp(nameof(MemoryBrush), typeof(Brush), Brushes.MediumPurple);
    public static readonly DependencyProperty DiskBrushProperty = Dp(nameof(DiskBrush), typeof(Brush), Brushes.Orange);
    public static readonly DependencyProperty TemperatureBrushProperty = Dp(nameof(TemperatureBrush), typeof(Brush), Brushes.IndianRed);
    public static readonly DependencyProperty GridBrushProperty = Dp(nameof(GridBrush), typeof(Brush), Brushes.DimGray);
    public static readonly DependencyProperty LabelBrushProperty = Dp(nameof(LabelBrush), typeof(Brush), Brushes.Gray);

    private static DependencyProperty Dp(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(PerformanceChart), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public IReadOnlyList<MetricSample>? Samples { get => (IReadOnlyList<MetricSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public bool ShowCpu { get => (bool)GetValue(ShowCpuProperty); set => SetValue(ShowCpuProperty, value); }
    public bool ShowMemory { get => (bool)GetValue(ShowMemoryProperty); set => SetValue(ShowMemoryProperty, value); }
    public bool ShowDisk { get => (bool)GetValue(ShowDiskProperty); set => SetValue(ShowDiskProperty, value); }
    public bool ShowTemperature { get => (bool)GetValue(ShowTemperatureProperty); set => SetValue(ShowTemperatureProperty, value); }
    public Brush CpuBrush { get => (Brush)GetValue(CpuBrushProperty); set => SetValue(CpuBrushProperty, value); }
    public Brush MemoryBrush { get => (Brush)GetValue(MemoryBrushProperty); set => SetValue(MemoryBrushProperty, value); }
    public Brush DiskBrush { get => (Brush)GetValue(DiskBrushProperty); set => SetValue(DiskBrushProperty, value); }
    public Brush TemperatureBrush { get => (Brush)GetValue(TemperatureBrushProperty); set => SetValue(TemperatureBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PerformanceChart chart && e.Property == SamplesProperty)
            AutomationProperties.SetHelpText(chart, chart.Describe());
    }

    /// <summary>Screen-reader summary of what the chart shows.</summary>
    private string Describe()
    {
        var s = Samples;
        if (s is null || s.Count == 0) return "No data yet.";
        return string.Format(CultureInfo.CurrentCulture, "Processor average {0:0}% (peak {1:0}%), memory average {2:0}%, over {3} samples.",
            s.Average(x => x.CpuPercent), s.Max(x => x.CpuPercent), s.Average(x => x.MemoryPercent), s.Count);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 30) return;

        const double left = 34, bottom = 18, top = 6, right = 6;
        var plot = new Rect(left, top, Math.Max(1, w - left - right), Math.Max(1, h - top - bottom));
        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();
        var typeface = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var pct in new[] { 0, 25, 50, 75, 100 })
        {
            var y = Math.Round(plot.Bottom - plot.Height * pct / 100) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = new FormattedText($"{pct}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, LabelBrush, dpi);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }

        var samples = Samples;
        if (samples is null || samples.Count < 2)
        {
            var wait = new FormattedText("Collecting measurements…", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, LabelBrush, dpi);
            dc.DrawText(wait, new Point(plot.Left + (plot.Width - wait.Width) / 2, plot.Top + (plot.Height - wait.Height) / 2));
            return;
        }

        var t0 = samples[0].Timestamp;
        var span = Math.Max(1, (samples[^1].Timestamp - t0).TotalSeconds);
        double X(MetricSample s) => plot.Left + (s.Timestamp - t0).TotalSeconds / span * plot.Width;

        if (ShowMemory) DrawSeries(dc, samples, X, s => s.MemoryPercent, 100, MemoryBrush, plot, fill: !ShowCpu);
        if (ShowDisk) DrawSeries(dc, samples, X, s => s.DiskActivePercent, 100, DiskBrush, plot, fill: false);
        if (ShowTemperature) DrawSeries(dc, samples, X, s => s.TemperatureCelsius, 100, TemperatureBrush, plot, fill: false);
        if (ShowCpu) DrawSeries(dc, samples, X, s => s.CpuPercent, 100, CpuBrush, plot, fill: true);

        var startLabel = new FormattedText(samples[0].Timestamp.LocalDateTime.ToString("t", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, LabelBrush, dpi);
        var endLabel = new FormattedText(samples[^1].Timestamp.LocalDateTime.ToString("t", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, LabelBrush, dpi);
        dc.DrawText(startLabel, new Point(plot.Left, plot.Bottom + 3));
        dc.DrawText(endLabel, new Point(plot.Right - endLabel.Width, plot.Bottom + 3));
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<MetricSample> samples, Func<MetricSample, double> x, Func<MetricSample, double?> value,
        double max, Brush brush, Rect plot, bool fill)
    {
        // Split into runs so gaps (null values) are shown as gaps, never interpolated.
        var runs = new List<List<Point>>();
        List<Point>? current = null;
        foreach (var s in samples)
        {
            var v = value(s);
            if (v is null)
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = [];
                runs.Add(current);
            }

            var y = plot.Bottom - Math.Clamp(v.Value / max, 0, 1) * plot.Height;
            current.Add(new Point(x(s), y));
        }

        var pen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        Brush? fillBrush = null;
        if (fill && brush is SolidColorBrush solid)
        {
            var c = solid.Color;
            fillBrush = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            fillBrush.Freeze();
        }

        foreach (var run in runs.Where(r => r.Count >= 2))
        {
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(run[0], false, false);
                ctx.PolyLineTo(run.Skip(1).ToList(), true, true);
            }

            line.Freeze();

            if (fillBrush is not null)
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(run[0].X, plot.Bottom), true, true);
                    ctx.PolyLineTo(run, false, true);
                    ctx.LineTo(new Point(run[^1].X, plot.Bottom), false, false);
                }

                area.Freeze();
                dc.DrawGeometry(fillBrush, null, area);
            }

            dc.DrawGeometry(null, pen, line);
        }
    }
}

/// <summary>Horizontal before/after bars for benchmark comparisons.</summary>
public sealed class ComparisonBars : FrameworkElement
{
    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(nameof(Before), typeof(double), typeof(ComparisonBars), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(nameof(After), typeof(double), typeof(ComparisonBars), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BeforeBrushProperty = DependencyProperty.Register(nameof(BeforeBrush), typeof(Brush), typeof(ComparisonBars), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AfterBrushProperty = DependencyProperty.Register(nameof(AfterBrush), typeof(Brush), typeof(ComparisonBars), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Before { get => (double)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public double After { get => (double)GetValue(AfterProperty); set => SetValue(AfterProperty, value); }
    public Brush BeforeBrush { get => (Brush)GetValue(BeforeBrushProperty); set => SetValue(BeforeBrushProperty, value); }
    public Brush AfterBrush { get => (Brush)GetValue(AfterBrushProperty); set => SetValue(AfterBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 18);

    protected override void OnRender(DrawingContext dc)
    {
        var max = Math.Max(Math.Max(Math.Abs(Before), Math.Abs(After)), 1e-9);
        var w = ActualWidth;
        const double barHeight = 7;
        dc.DrawRoundedRectangle(BeforeBrush, null, new Rect(0, 0, Math.Max(2, w * Math.Abs(Before) / max), barHeight), 3, 3);
        dc.DrawRoundedRectangle(AfterBrush, null, new Rect(0, barHeight + 3, Math.Max(2, w * Math.Abs(After) / max), barHeight), 3, 3);
    }
}
