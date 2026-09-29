using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tickshift.App.Controls;

/// <summary>A stroked 24×24 line icon. Colour follows Foreground, so it adapts to theme and state.</summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}

public enum BadgeKind { Neutral, Accent, Good, Warn, Bad, Info }

/// <summary>Status pill. Always shows text (never colour alone) for accessibility.</summary>
public sealed class StatusBadge : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(BadgeKind), typeof(StatusBadge), new PropertyMetadata(BadgeKind.Neutral));

    static StatusBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(typeof(StatusBadge)));
        FocusableProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(false));
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public BadgeKind Kind
    {
        get => (BadgeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}

/// <summary>Dashboard metric tile with optional ring gauge.</summary>
public sealed class MetricCard : Control
{
    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), typeof(string), string.Empty);
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), typeof(string), "—");
    public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), typeof(string), string.Empty);
    public static readonly DependencyProperty CaptionProperty = Register(nameof(Caption), typeof(string), string.Empty);
    public static readonly DependencyProperty PercentProperty = Register(nameof(Percent), typeof(double), -1d);
    public static readonly DependencyProperty IconDataProperty = Register(nameof(IconData), typeof(Geometry), null);
    public static readonly DependencyProperty StatusProperty = Register(nameof(Status), typeof(BadgeKind), BadgeKind.Accent);

    static MetricCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(MetricCard), new FrameworkPropertyMetadata(typeof(MetricCard)));
        FocusableProperty.OverrideMetadata(typeof(MetricCard), new FrameworkPropertyMetadata(false));
    }

    private static DependencyProperty Register(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(MetricCard), new PropertyMetadata(def));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Geometry? IconData { get => (Geometry?)GetValue(IconDataProperty); set => SetValue(IconDataProperty, value); }
    public BadgeKind Status { get => (BadgeKind)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
}

/// <summary>
/// Lays children out in equal-width columns; the column count adapts to the available width so pages
/// never scroll horizontally on 1366×768 displays.
/// </summary>
public sealed class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(220d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private int Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return 1;
        var cols = (int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing));
        return Math.Clamp(cols, 1, Math.Max(1, MaxColumns));
    }

    protected override Size MeasureOverride(Size available)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var cols = Columns(available.Width);
        var colWidth = double.IsInfinity(available.Width) ? MinColumnWidth : (available.Width - Spacing * (cols - 1)) / cols;
        double height = 0, rowHeight = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(Math.Max(0, colWidth), double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            if ((i + 1) % cols == 0 || i == visible.Count - 1)
            {
                height += rowHeight + (i == visible.Count - 1 ? 0 : Spacing);
                rowHeight = 0;
            }
        }

        var width = double.IsInfinity(available.Width) ? cols * MinColumnWidth + (cols - 1) * Spacing : available.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var cols = Columns(finalSize.Width);
        var colWidth = (finalSize.Width - Spacing * (cols - 1)) / cols;
        double y = 0;
        for (var start = 0; start < visible.Count; start += cols)
        {
            var row = visible.Skip(start).Take(cols).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++)
                row[i].Arrange(new Rect(i * (colWidth + Spacing), y, Math.Max(0, colWidth), rowHeight));
            y += rowHeight + Spacing;
        }

        return finalSize;
    }
}
