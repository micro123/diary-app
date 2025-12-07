using Diary.Database;

namespace Diary.Db.PostgreSQL;

public sealed class PostgreSQLFactory: IDbFactory
{
    public string Name => "PostgreSQL";
    public bool Usable => false;
    public DbInterfaceBase Create()
    {
        return new PgDb(this);
    }

    public Migration? GetMigration(uint version)
    {
        throw new NotImplementedException();
    }
}