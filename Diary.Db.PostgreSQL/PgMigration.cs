using Diary.Database;

namespace Diary.Db.PostgreSQL;

public class PgMigration(uint from, uint to, params string[] statements) : Migration(from, to)
{
    private readonly string _stmts = string.Join("\n;", statements);
    
    public override bool Up(DbInterfaceBase db)
    {
        var pg = db as PgDb;
        // TODO: exec _stmts
        return true;
    }
}
