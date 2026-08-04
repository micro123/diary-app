using System.Data.Common;

namespace Diary.PluginBase;

public sealed class DelegatePluginMigrationContext(
    string providerName,
    uint coreDataVersion,
    Func<string, bool> exec,
    Func<string, Func<DbDataReader, object?>, object[], IEnumerable<object?>> query)
    : IPluginMigrationContext
{
    public string ProviderName => providerName;
    public uint CoreDataVersion => coreDataVersion;

    public bool ExecRaw(string sql) => exec(sql);

    public List<T> Query<T>(string sql, Func<DbDataReader, T> map, params object[] args)
    {
        return query(sql, reader => map(reader), args).Cast<T>().ToList();
    }
}
