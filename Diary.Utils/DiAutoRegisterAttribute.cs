namespace Diary.Utils;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class DiAutoRegisterAttribute(Type? serviceType = null, string key = "", bool singleton = false): Attribute()
{
    public Type? ServiceType { get; } = serviceType;
    public string Key { get; } = key;
    public bool Singleton { get; } = singleton;
}
