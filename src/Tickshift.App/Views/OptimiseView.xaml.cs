using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace Tickshift.App.Views;

public partial class OptimiseView : UserControl
{
    public OptimiseView() => InitializeComponent();
}

/// <summary>True when a pipeline step number has been reached by the current step.</summary>
public sealed class StepReachedConverter : IMultiValueConverter
{
    public static StepReachedConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [int step, int current] && current > 0 && step <= current;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
