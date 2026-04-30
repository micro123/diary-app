using System.Data;
using System.Data.SQLite;
using Diary.Database;

namespace Diary.MigrationTool.Impl;

internal sealed class SqliteMigrator : BaseMigrator
{
    public SqliteMigrator(DbInterfaceBase db, string oldDatabase, Action<bool, double, string> processCallback)
        : base(db, OpenConnection(oldDatabase), processCallback)
    {
    }

    private static SQLiteConnection OpenConnection(string oldDatabase)
    {
        var csb = new SQLiteConnectionStringBuilder()
        {
            DataSource = oldDatabase,
            ReadOnly = true,
        };
        var connection = new SQLiteConnection(csb.ConnectionString);
        connection.Open();
        return connection;
    }

    protected override string ReadDate(IDataReader reader, int ordinal) =>
        reader.GetString(ordinal);

    protected override int ReadColor(IDataReader reader, int ordinal) =>
        reader.GetInt32(ordinal);
}
