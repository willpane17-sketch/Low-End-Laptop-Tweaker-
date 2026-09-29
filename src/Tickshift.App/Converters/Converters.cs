using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Tickshift.App.Controls;
using Tickshift.Core;
using Tickshift.Core.Models;
using Tickshift.Diagnostics.Benchmarks;
using Tickshift.Optimization;
using Tickshift.Persistence;
using Tickshift.Trading;

namespace Tickshift.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value switch
        {
            bool b => b,
            int i => i != 0,
            string s => !string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count > 0,
            null => false,
            _ => true,
        };
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Two-way binding between an enum property and a RadioButton (ConverterParameter = enum member name).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string p) return Binding.DoNothing;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum ? Enum.Parse(type, p) : p; // string-valued "tabs" and filters are supported too
    }
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => Format.Bytes(l),
        ulong u => Format.Bytes(u),
        int i => Format.Bytes(i),
        double d => Format.Bytes((long)d),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TimestampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset d => d.LocalDateTime.ToString(parameter as string ?? "g", culture),
        DateTime d => d.ToString(parameter as string ?? "g", culture),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Maps domain states to badge colours. Text is always shown alongside, never colour alone.</summary>
public sealed class BadgeKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Severity s => s switch
        {
            Severity.Critical or Severity.High => BadgeKind.Bad,
            Severity.Medium => BadgeKind.Warn,
            Severity.Low => BadgeKind.Info,
            _ => BadgeKind.Neutral,
        },
        RiskLevel r => r switch { RiskLevel.Low => BadgeKind.Good, RiskLevel.Moderate => BadgeKind.Warn, _ => BadgeKind.Bad },
        Reversibility rv => rv switch { Reversibility.FullyReversible => BadgeKind.Good, Reversibility.PartiallyReversible => BadgeKind.Warn, _ => BadgeKind.Neutral },
        ThermalState t => t switch
        {
            ThermalState.Normal => BadgeKind.Good,
            ThermalState.Elevated => BadgeKind.Warn,
            ThermalState.High or ThermalState.Critical => BadgeKind.Bad,
            _ => BadgeKind.Neutral,
        },
        SystemCondition c => c switch
        {
            SystemCondition.Good => BadgeKind.Good,
            SystemCondition.Fair => BadgeKind.Info,
            SystemCondition.Strained => BadgeKind.Warn,
            SystemCondition.Critical => BadgeKind.Bad,
            _ => BadgeKind.Neutral,
        },
        ChangeStatus cs => cs switch
        {
            ChangeStatus.Verified or ChangeStatus.Completed => BadgeKind.Good,
            ChangeStatus.Applied => BadgeKind.Info,
            ChangeStatus.RolledBack => BadgeKind.Accent,
            ChangeStatus.Superseded => BadgeKind.Neutral,
            ChangeStatus.Pending or ChangeStatus.Indeterminate or ChangeStatus.VerificationFailed => BadgeKind.Warn,
            _ => BadgeKind.Bad,
        },
        RunItemStatus rs => rs switch
        {
            RunItemStatus.Verified => BadgeKind.Good,
            RunItemStatus.DryRun => BadgeKind.Info,
            RunItemStatus.AppliedNotVerified => BadgeKind.Warn,
            RunItemStatus.Failed or RunItemStatus.Rejected => BadgeKind.Bad,
            _ => BadgeKind.Neutral,
        },
        DeltaAssessment d => d switch
        {
            DeltaAssessment.Improved => BadgeKind.Good,
            DeltaAssessment.Worse => BadgeKind.Warn,
            _ => BadgeKind.Neutral,
        },
        PressureLevel p => p switch { PressureLevel.Normal => BadgeKind.Good, PressureLevel.Elevated => BadgeKind.Warn, _ => BadgeKind.Bad },
        ProcessCategory pc => pc switch
        {
            ProcessCategory.ProtectedTrading => BadgeKind.Accent,
            ProcessCategory.Critical or ProcessCategory.Security => BadgeKind.Info,
            ProcessCategory.WindowsComponent => BadgeKind.Neutral,
            _ => BadgeKind.Neutral,
        },
        SignatureState sig => sig switch { SignatureState.Valid => BadgeKind.Good, SignatureState.Invalid => BadgeKind.Bad, _ => BadgeKind.Neutral },
        bool b => b ? BadgeKind.Good : BadgeKind.Neutral,
        _ => BadgeKind.Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Human wording for enum values shown in badges.</summary>
public sealed class FriendlyTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RiskLevel r => $"{r} risk",
        Reversibility.FullyReversible => "Reversible",
        Reversibility.PartiallyReversible => "Partly reversible",
        Reversibility.NotReversible => "Not reversible",
        ChangeStatus.VerificationFailed => "Not verified",
        ChangeStatus.RolledBack => "Restored",
        ChangeStatus.RollbackFailed => "Restore failed",
        ChangeStatus.Superseded => "Changed elsewhere",
        ChangeStatus.Completed => "Done (permanent)",
        RunItemStatus.AppliedNotVerified => "Not verified",
        RunItemStatus.NotApplicable => "Not applicable",
        RunItemStatus.DryRun => "Dry run",
        ProcessCategory.ProtectedTrading => "Protected",
        ProcessCategory.WindowsComponent => "Windows",
        ProcessCategory.Critical => "System",
        SignatureState.Valid => "Signed",
        SignatureState.NotSigned => "No embedded signature",
        SignatureState.Invalid => "Invalid signature",
        SignatureState.Unknown or SignatureState.NotChecked => "Signature unknown",
        PrivilegeLevel.Administrator => "Needs administrator",
        PrivilegeLevel.StandardUser => "No admin needed",
        null => string.Empty,
        _ => SplitWords(value.ToString() ?? string.Empty),
    };

    private static string SplitWords(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]) ? char.ToLowerInvariant(s[i]) : s[i]);
        }

        return sb.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Multiplies a 0–1 or 0–100 value into a pixel width (ConverterParameter = full width).</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var full = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 100;
        var v = value switch { double d => d, int i => i, float fl => fl, _ => 0 };
        return Math.Clamp(v / 100.0, 0, 1) * full;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
