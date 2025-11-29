using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Diary.App.Converters;

public class Int2Color: IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int rgb)
        {
            var (r, g, b) = FromInt(rgb);
            var color = new Color(0xFF, r, g, b);
            return new SolidColorBrush(color);
        }
        return AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }

    public static (byte r, byte g, byte b) FromInt(int rgb)
    {
        var r = (byte)(rgb >> 16);
        var g = (byte)(rgb >> 8);
        var b = (byte)(rgb >> 0);
        return (r, g, b);
    }
}