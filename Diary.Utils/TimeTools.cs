namespace Diary.Utils;

public enum AdjustPart
{
    Week,
    Month,
    Quarter,
    Year,
}

public enum AdjustDirection
{
    Previous,
    Current,
    Next,
}

public static class TimeTools
{
    public static string FormatDateTime(DateTime dateTime)
    {
        return dateTime.ToString("yyyy-MM-dd");
    }

    public static DateTime FromFormatedDate(string date)
    {
        if (DateTime.TryParse(date, out var dateTime))
        {
            return dateTime;
        }

        throw new FormatException();
    }

    public static string Today()
    {
        return FormatDateTime(DateTime.Today);
    }

    public static string Yestoday()
    {
        return FormatDateTime(DateTime.Today.AddDays(-1));
    }

    public static string Tomorrow()
    {
        return FormatDateTime(DateTime.Today.AddDays(1));
    }

    public static int GetWeekName(string date)
    {
        if (DateTime.TryParse(date, out var dateTime))
        {
            return (int)dateTime.DayOfWeek;
        }

        return -1;
    }

    private static (DateTime, DateTime) CalculateRange(DateTime input, AdjustPart part, AdjustDirection direction)
    {
        DateTime start, end;
        switch (part)
        {
            case AdjustPart.Year:
            {
                start = new DateTime(input.Year, 1, 1);
                end = new DateTime(input.Year, 12, 31);
            }
                break;
            case AdjustPart.Quarter:
            {
                int q = (input.Month - 1) / 3;
                start = new DateTime(input.Year, q*3+1, 1);
                end = start.AddMonths(3).AddDays(-1);
            }
                break;
            case AdjustPart.Month:
            {
                start = new DateTime(input.Year, input.Month, 1);
                end = start.AddMonths(1).AddDays(-1);
            }
                break;
            case AdjustPart.Week:
            {
                int w = (int)input.DayOfWeek;
                start = input.Date.AddDays(-w + 1);
                end = start.AddDays(6);
            }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(part), part, null);
        }
        if (direction == AdjustDirection.Previous)
        {
            switch (part)
            {
                case AdjustPart.Year:
                    start = start.AddYears(-1);
                    end = end.AddYears(-1);
                    break;
                case AdjustPart.Quarter:
                    start = start.AddMonths(-3);
                    end = end.AddMonths(-3);
                    break;
                case AdjustPart.Month:
                    start = start.AddMonths(-1);
                    end = end.AddMonths(-1);
                    break;
                case AdjustPart.Week:
                    start = start.AddDays(-7);
                    end = end.AddDays(-7);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(part), part, null);
            }
        }
        return (start, end);
    }
    
    public static void AdjustDate(ref DateTime start, ref DateTime end, AdjustPart part, AdjustDirection dir)
    {
        (start, end) = dir switch
        {
            AdjustDirection.Current or AdjustDirection.Previous => CalculateRange(start, part, dir),
            AdjustDirection.Next => CalculateRange(end.AddDays(1), part, AdjustDirection.Current),
            _ => (start, end)
        };
    }
}