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

    private static string FormatDateTime(DateTime dateTime)
    {
        return dateTime.ToString("yyyy-MM-dd");
    }

    static public string Today()
    {
        return FormatDateTime(DateTime.Today);
    }

    static public string Yestoday()
    {
        return FormatDateTime(DateTime.Today.AddDays(-1));
    }

    static public string Tomorrow()
    {
        return FormatDateTime(DateTime.Today.AddDays(1));
    }

    static public int GetWeekName(string date)
    {
        if (DateTime.TryParse(date, out var dateTime))
        {
            return (int)dateTime.DayOfWeek;
        }
        return -1;
    }

    static public void CompletionDate(string prefix, out string start, out string end)
    {
        throw new NotImplementedException();
    }

    static public void AdjustDate(ref string start, ref string end, AdjustPart part, AdjustDirection dir)
    {
        throw new NotImplementedException();
    }
}