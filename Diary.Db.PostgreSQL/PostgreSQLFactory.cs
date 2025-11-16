using Diary.Database;

namespace Diary.Db.PostgreSQL;

public class PostgreSQLFactory: IDbFactory
{
    public string Name => "PostgreSQL";
    public IDbInterface Create()
    {
        return new PgDb();
    }
}