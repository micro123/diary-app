using Diary.Database;

namespace Diary.Db.PostgreSQL;

public class PostgreSQLFactory: IDbFactory
{
    public string Name => "PostgreSQL";
    public DbInterfaceBase Create()
    {
        return new PgDb(this);
    }

    public Migration? GetMigration(uint version)
    {
        throw new NotImplementedException();
    }
}