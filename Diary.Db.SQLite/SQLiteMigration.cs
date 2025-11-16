using Diary.Database;

namespace Diary.Db.SQLite;

internal class SQLiteMigration : Migration
{
    private readonly ICollection<string> _up;
    private readonly ICollection<string> _down;

    public SQLiteMigration(int from, int to, ICollection<string> upStmts, ICollection<string> downStmts)
        :base(from, to)
    {
        _up = upStmts;
        _down = downStmts;
    }

    public override bool Down(IDbInterface db)
    {
        var sqlite = db as SQLiteDb;
        return sqlite!.Exec(_down);
    }

    public override bool Up(IDbInterface db)
    {
        var sqlite = db as SQLiteDb;
        return sqlite!.Exec(_up);
    }
}
