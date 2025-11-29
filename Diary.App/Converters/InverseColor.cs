using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Diary.App.Converters;

public class InverseColor: IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int rgb)
        {
            var (r,g,b) = Int2Color.FromInt(rgb);
            return IsLight(r, g, b) ? new SolidColorBrush(Color.FromRgb(0, 0, 0)) : new SolidColorBrush(Color.FromRgb(255,255,255));
        }
        return AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }

    private static bool IsLight(byte r, byte g, byte b)
    {
        // 将RGB值从0-255缩放到0-1
        var rSrgb = r / 255.0;
        var gSrgb = g / 255.0;
        var bSrgb = b / 255.0;

        // 应用sRGB伽马校正
        var rLinear = rSrgb > 0.04045 ? Math.Pow((rSrgb + 0.055) / 1.055, 2.4) : rSrgb / 12.92;
        var gLinear = gSrgb > 0.04045 ? Math.Pow((gSrgb + 0.055) / 1.055, 2.4) : gSrgb / 12.92;
        var bLinear = bSrgb > 0.04045 ? Math.Pow((bSrgb + 0.055) / 1.055, 2.4) : bSrgb / 12.92;

        // 计算亮度（WCAG公式）
        var level = 0.2126 * rLinear + 0.7152 * gLinear + 0.0722 * bLinear;
        return level >= 0.5;
    }
}