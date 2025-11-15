using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Data.Converters;
using Diary.Utils;

namespace Diary.App.Converters;

// date <=> string
public class DateConvert: IValueConverter
{
    private static string? DateToString(DateTime? date)
    {
        return date?.ToString("yyyy-MM-dd");
    }

    private static DateTime? StringToDate(string date)
    {
        if (DateTime.TryParse(date, out var dateResult))
        {
            return dateResult;
        }
        return null;
    }
    
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = targetType.RemoveNullable();
        Debug.Assert(type == typeof(DateTime) || type == typeof(string));
        return value switch
        {
            DateTime date => DateToString(date),
            string s => StringToDate(s),
            _ => null
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = targetType.RemoveNullable();
        Debug.Assert(type == typeof(DateTime) || type == typeof(string));
        return value switch
        {
            DateTime date => DateToString(date),
            string s => StringToDate(s),
            _ => null
        };
    }
}