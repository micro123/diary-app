using System.Data;
using Diary.Database;
using Diary.Utils;
using Npgsql;

namespace Diary.MigrationTool.Impl;

internal sealed class PgMigrator : BaseMigrator
{
    private readonly NpgsqlDataSource _dataSource;

    private PgMigrator(DbInterfaceBase db, NpgsqlConnection connection, NpgsqlDataSource dataSource, Action<bool, double, string> processCallback)
        : base(db, connection, processCallback)
    {
        _dataSource = dataSource;
    }

    public static PgMigrator Create(DbInterfaceBase db, string host, ushort port, string database, string user, string password, Action<bool, double, string> processCallback)
    {
        var csb = new NpgsqlConnectionStringBuilder()
        {
            Host = host,
            Port = port,
            Database = database,
            Username = user,
            Password = password,
        };
        var dataSource = new NpgsqlDataSourceBuilder(csb.ConnectionString).Build();
        var connection = dataSource.OpenConnection();
        return new PgMigrator(db, connection, dataSource, processCallback);
    }

    protected override string ReadDate(IDataReader reader, int ordinal) =>
        TimeTools.FormatDateTime(reader.GetDateTime(ordinal));

    protected override long ReadColorValue(IDataReader reader, int ordinal) =>
        Convert.ToInt64(reader.GetValue(ordinal));

    public override void Dispose()
    {
        _dataSource.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
