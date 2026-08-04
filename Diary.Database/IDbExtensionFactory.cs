namespace Diary.Database;

public interface IDbExtensionFactory
{
    bool Supports(Type extensionType, string providerName);
    object? Create(IDbExtensionHost host, string instanceId);
}
