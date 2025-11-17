using Diary.Database;

namespace Diary.Db.SQLite;

internal class SQLiteMigration : Migration
{
    private readonly string _up;

    public SQLiteMigration(uint from, uint to, params string[] upStmts)
        :base(from, to)
    {
        _up = string.Join(";\n", upStmts);
    }

    public override bool Up(DbInterfaceBase db)
    {
        var sqlite = db as SQLiteDb;
        // return sqlite!.Exec(_up);
        return false;
    }
}
