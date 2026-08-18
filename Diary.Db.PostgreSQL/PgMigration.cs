using System.Security.Cryptography;
using System.Text;
using Diary.Database;

namespace Diary.Db.PostgreSQL;

public class PgMigration(uint from, uint to, params string[] statements) : Migration(from, to)
{
    private readonly string _stmts = string.Join("\n;", statements);

    public override string Checksum => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(_stmts)));

    public override bool Up(DbInterfaceBase db)
    {
        if (db is not PgDb pg)
            return false;
        return pg.ExecRaw(_stmts);
    }
}
