using System.Data.Common;

namespace Diary.App.Services;

internal static class GlobalExceptionPolicy
{
    public static bool CanContinue(Exception exception) => exception is DbException;
}
