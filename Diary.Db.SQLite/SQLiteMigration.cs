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
        if (db is not SQLiteDb sqlite)
            return false;
        return sqlite.ExecRaw(_up);
    }
}
