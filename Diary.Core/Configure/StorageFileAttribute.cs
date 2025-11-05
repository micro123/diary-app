namespace Diary.Core.Configure;

[AttributeUsage(AttributeTargets.Class|AttributeTargets.Struct, AllowMultiple = false)]
public class StorageFileAttribute(string fileName, string encryptKey = "") : Attribute
{
    public string FileName { get; } = fileName;
    public string EncryptKey { get; } = encryptKey;
    public bool Encrypted { get; } = !string.IsNullOrEmpty(encryptKey);
}
