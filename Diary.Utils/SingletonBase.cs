using BindingFlags = System.Reflection.BindingFlags;

namespace Diary.Utils;

public class SingletonBase<T> where T : class
{
    private static readonly Lazy<T> _instance = new(() =>
    {
        var type = typeof(T);
        var ctor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            Type.EmptyTypes,
            null
            );
        if (ctor == null)
            throw new InvalidOperationException($"NO default constructor found for {typeof(T).FullName}");
        if (ctor.IsPublic)
            throw new InvalidOperationException("ctor must NOT be public");
        
        return (T)ctor.Invoke(null);
    });
    
    protected SingletonBase() {}
    
    public static T Instance => _instance.Value;
}