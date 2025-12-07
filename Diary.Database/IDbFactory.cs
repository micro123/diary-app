namespace Diary.Database;

public interface IDbFactory
{
    string Name { get; }
    
    bool Usable { get; }

    DbInterfaceBase Create();
    
    Migration? GetMigration(uint version);
}
