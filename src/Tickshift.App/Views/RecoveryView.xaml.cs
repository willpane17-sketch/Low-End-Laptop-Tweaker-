using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Tickshift.App.Views;

public partial class RecoveryView : UserControl
{
    public RecoveryView() => InitializeComponent();
}

/// <summary>Visible when the bound value's text equals the ConverterParameter (used for simple tab switching).</summary>
public sealed class EqualsVisibilityConverter : IValueConverter
{
    public static EqualsVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
