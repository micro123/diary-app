using Diary.Database;

namespace Diary.Db.PostgreSQL;

public sealed class PostgreSQLFactory: IDbFactory
{
    private readonly Config _config = new();
    public string Name => "PostgreSQL";
    public bool Usable => true;
    public DbInterfaceBase Create()
    {
        return new PgDb(this);
    }

    public Migration? GetMigration(uint version)
    {
        throw new NotImplementedException();
    }

    public object GetConfig()
    {
        return _config;
    }
}