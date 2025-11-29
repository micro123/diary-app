using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Diary.Core.Data.Base;

namespace Diary.App.Converters;

public class TagLevelConverter:IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TagLevels lv)
        {
            return lv == TagLevels.Primary ? "【主】" : "【次】";
        }
        return AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}