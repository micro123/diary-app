namespace Diary.Database;

public interface IDbFactory
{
    string Name { get; }

    IDbInterface Create();
}
