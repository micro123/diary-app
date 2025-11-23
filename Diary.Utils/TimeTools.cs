namespace Diary.Utils;

public static class TimeTools
{
    public enum AdjustPart
    {
        Year,
        Quarter,
        Month,
        Week,
    }

    public enum AdjustDirection
    {
        Previous,
        Current,
        Next,
    }

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

    public static void CompletionDate(string prefix, out string start, out string end)
    {
        throw new NotImplementedException();
    }

    public static void AdjustDate(ref string start, ref string end, AdjustPart part, AdjustDirection dir)
    {
        throw new NotImplementedException();
    }
}