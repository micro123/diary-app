using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Diary.App.Converters;

// hsv <=> int (AARRGGBB)
public class HsvColorConverter: IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            int rgb => ToHsv(rgb),
            HsvColor hsv => FromHsv(hsv),
            _ => AvaloniaProperty.UnsetValue
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            int rgb => ToHsv(rgb),
            HsvColor hsv => FromHsv(hsv),
            _ => AvaloniaProperty.UnsetValue
        };
    }

    public static int FromHsv(HsvColor hsv)
    {
        double h = hsv.H;
        double s = hsv.S;
        double v = hsv.V;
        
        h %= 360;
        if (h < 0) h += 360;
    
        s = Math.Max(0, Math.Min(1, s));
        v = Math.Max(0, Math.Min(1, v));
    
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
    
        double r, g, b;
    
        if (h >= 0 && h < 60)
        {
            r = c; g = x; b = 0;
        }
        else if (h >= 60 && h < 120)
        {
            r = x; g = c; b = 0;
        }
        else if (h >= 120 && h < 180)
        {
            r = 0; g = c; b = x;
        }
        else if (h >= 180 && h < 240)
        {
            r = 0; g = x; b = c;
        }
        else if (h >= 240 && h < 300)
        {
            r = x; g = 0; b = c;
        }
        else // h >= 300 && h < 360
        {
            r = c; g = 0; b = x;
        }

        return (byte)((r + m) * 255) * 0x10000 + (byte)((g + m) * 255) * 0x100 + (byte)((b + m) * 255);
    }

    public static HsvColor ToHsv(int rgb)
    {
        double r = ((rgb & 0xFF0000) >> 16) / 255.0;
        double g = ((rgb & 0xFF00) >> 8) / 255.0;
        double b = ((rgb & 0xFF) >> 0) / 255.0;
    
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
    
        double h = 0;
        double s = max == 0 ? 0 : delta / max;
        double v = max;
    
        if (delta != 0)
        {
            if (Math.Abs(max - r) < 1e-9)
            {
                h = 60 * (((g - b) / delta) % 6);
            }
            else if (Math.Abs(max - g) < 1e-9)
            {
                h = 60 * (((b - r) / delta) + 2);
            }
            else if (Math.Abs(max - b) < 1e-9)
            {
                h = 60 * (((r - g) / delta) + 4);
            }
        }
    
        if (h < 0) h += 360;
    
        return new HsvColor(1.0, h, s, v);
    }
}