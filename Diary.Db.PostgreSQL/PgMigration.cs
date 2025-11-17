using Diary.Database;

namespace Diary.Db.PostgreSQL;

public class PgMigration(uint from, uint to) : Migration(from, to)
{
    public override bool Up(DbInterfaceBase db)
    {
        throw new NotImplementedException();
    }
}
