using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Diary.App.Converters;

public class State2IconConverter: IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            bool v => v ? "mdi-check-circle-outline" : "mdi-radiobox-blank",
            _ => AvaloniaProperty.UnsetValue
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}