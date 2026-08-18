using System.Security.Cryptography;
using System.Text;
using Diary.Database;

namespace Diary.Db.SQLite;

internal class SQLiteMigration : Migration
{
    private readonly string _up;

    public SQLiteMigration(uint from, uint to, params string[] upStmts)
        : base(from, to)
    {
        _up = string.Join(";\n", upStmts);
    }

    public override string Checksum => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(_up)));

    public override bool Up(DbInterfaceBase db)
    {
        if (db is not SQLiteDb sqlite)
            return false;
        return sqlite.ExecRaw(_up);
    }
}
