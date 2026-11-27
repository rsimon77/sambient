using System.Globalization;
using Avalonia.Data.Converters;

namespace Sambient.App.ViewModels;

/// <summary>bool -> opacity for blinking indicators (true = fully visible, false = dimmed).</summary>
public sealed class BlinkConverter : IValueConverter
{
    public static readonly BlinkConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1.0 : 0.25;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
