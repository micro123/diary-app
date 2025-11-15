using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Data.Converters;
using Diary.Utils;

namespace Diary.App.Converters;

public class EnumConvert: IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = targetType.RemoveNullable();
        Debug.Assert(type.IsEnum || type == typeof(string));
        return value switch
        {
            string s => Enum.TryParse(type, s, out var result) ? result : null,
            _ => value!.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = targetType.RemoveNullable();
        Debug.Assert(type.IsEnum || type == typeof(string));
        return value switch
        {
            string s => Enum.TryParse(type, s, out var result) ? result : null,
            _ => value!.ToString()
        };
    }
}