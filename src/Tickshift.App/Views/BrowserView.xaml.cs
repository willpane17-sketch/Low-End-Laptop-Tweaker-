using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace Tickshift.App.Views;

public partial class BrowserView : UserControl
{
    public BrowserView() => InitializeComponent();
}

/// <summary>Turns a zero-based alternation index into a 1-based step number.</summary>
public sealed class PlusOneConverter : IValueConverter
{
    public static PlusOneConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i ? (i + 1).ToString(culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
