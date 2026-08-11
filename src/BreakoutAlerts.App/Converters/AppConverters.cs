using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BreakoutAlerts.Core.Models;

namespace BreakoutAlerts.App.Converters;

/// <summary>
/// Maps a signed number to the up or down brush.
/// </summary>
/// <remarks>
/// Resolves brushes from the application resource dictionary rather than constructing
/// colours here, so the palette stays defined in exactly one place - AppTheme.xaml - and
/// a future light theme needs no code change.
/// </remarks>
public sealed class SignToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isPositive = value switch
        {
            decimal d => d >= 0,
            double dbl => dbl >= 0,
            int i => i >= 0,
            _ => true
        };

        return LookupBrush(isPositive ? "AppUpBrush" : "AppDownBrush");
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("SignToBrushConverter is one-way.");

    internal static Brush LookupBrush(string key)
    {
        // TryFindResource rather than an indexer: at design time in the XAML previewer the
        // application resources are not loaded, and an indexer lookup throws, which shows
        // up as a broken designer rather than a graceful fallback.
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }
}

/// <summary>Maps a boolean to the up brush when true and the down brush when false.</summary>
public sealed class BoolToDirectionBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SignToBrushConverter.LookupBrush(value is true ? "AppUpBrush" : "AppDownBrush");

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("BoolToDirectionBrushConverter is one-way.");
}

/// <summary>Maps a <see cref="RangePosition"/> to a status brush.</summary>
public sealed class RangePositionToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SignToBrushConverter.LookupBrush(value switch
        {
            RangePosition.Above => "AppUpBrush",
            RangePosition.Below => "AppDownBrush",
            RangePosition.Inside => "AppTextSecondaryBrush",
            _ => "AppTextTertiaryBrush"
        });

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("RangePositionToBrushConverter is one-way.");
}

/// <summary>Maps a <see cref="RangePosition"/> to short display text.</summary>
public sealed class RangePositionToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            RangePosition.Above => "Above",
            RangePosition.Below => "Below",
            RangePosition.Inside => "Inside",
            _ => "—"
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("RangePositionToTextConverter is one-way.");
}

/// <summary>
/// Formats a nullable number, rendering null as an em dash.
/// </summary>
/// <remarks>
/// A null level is genuinely absent - a symbol with no extended-hours data has no
/// premarket high - and must not display as 0.00, which reads as a real price. The em
/// dash makes "no data" visually distinct from "zero".
/// </remarks>
public sealed class NullableNumberConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return "—";
        }

        var format = parameter as string ?? "N2";
        return value is IFormattable formattable
            ? formattable.ToString(format, culture)
            : value.ToString() ?? "—";
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("NullableNumberConverter is one-way.");
}

/// <summary>Formats a proportion as a signed percentage, e.g. 0.0178 becomes "+1.78%".</summary>
public sealed class SignedPercentConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var proportion = value switch
        {
            double d => d,
            decimal dec => (double)dec,
            _ => 0d
        };

        // Explicit sign on the positive case. Without it a rising and a falling row differ
        // only by a hyphen, which is easy to misread at a glance on a moving grid.
        var sign = proportion >= 0 ? "+" : string.Empty;
        return $"{sign}{proportion * 100:F2}%";
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("SignedPercentConverter is one-way.");
}

/// <summary>Formats a share count compactly - 24.4M rather than 24,400,000.</summary>
public sealed class CompactNumberConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var number = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L
        };

        return number switch
        {
            >= 1_000_000_000 => $"{number / 1_000_000_000d:F2}B",
            >= 1_000_000 => $"{number / 1_000_000d:F2}M",
            >= 1_000 => $"{number / 1_000d:F1}K",
            _ => number.ToString("N0", culture)
        };
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("CompactNumberConverter is one-way.");
}

/// <summary>Maps a boolean to <see cref="Visibility"/>, collapsing when false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        // "Invert" lets one converter serve both polarities instead of needing a second
        // class that differs only in a negation.
        if (parameter as string == "Invert")
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("BoolToVisibilityConverter is one-way.");
}
