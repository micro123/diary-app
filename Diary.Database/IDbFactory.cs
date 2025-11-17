namespace Diary.Database;

public interface IDbFactory
{
    string Name { get; }

    DbInterfaceBase Create();
    
    Migration? GetMigration(uint version);
}
