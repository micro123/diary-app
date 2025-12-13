using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Diary.Utils;

namespace Diary.App.Converters;

// date <=> string
public class DateConvert: IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = targetType.RemoveNullable();
        Debug.Assert(type == typeof(DateTime) || type == typeof(string));
        return value switch
        {
            DateTime date => TimeTools.FormatDateTime(date),
            string s => TimeTools.FromFormatedDate(s),
            _ => AvaloniaProperty.UnsetValue,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = targetType.RemoveNullable();
        Debug.Assert(type == typeof(DateTime) || type == typeof(string));
        return value switch
        {
            DateTime date => TimeTools.FormatDateTime(date),
            string s => TimeTools.FromFormatedDate(s),
            _ => AvaloniaProperty.UnsetValue,
        };
    }
}