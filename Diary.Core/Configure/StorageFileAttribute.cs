namespace Diary.Core.Configure;

[AttributeUsage(AttributeTargets.Class|AttributeTargets.Struct, AllowMultiple = false)]
public class StorageFileAttribute(string fileName) : Attribute
{
    public string FileName { get; } = fileName;
}
