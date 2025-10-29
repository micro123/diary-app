using Diary.Database;

namespace Diary.PostgreSQL;

public class PgMigration : Migration
{
    public PgMigration(int from, int to) : base(from, to)
    {
    }

    public override bool Down(IDbInterface db)
    {
        throw new NotImplementedException();
    }

    public override bool Up(IDbInterface db)
    {
        throw new NotImplementedException();
    }
}
