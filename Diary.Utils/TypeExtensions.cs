using System.Diagnostics;

namespace Diary.Utils;

public static class TypeExtensions
{
    public static Type RemoveNullable(this Type type)
    {
        Debug.Assert(type != null);
        return Nullable.GetUnderlyingType(type) ?? type;
    }
}