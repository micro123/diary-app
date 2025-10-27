namespace Diary.Database;

public abstract class Migration(int from, int to)
{
    public int VersionFrom { get; } = from;
    public int VersionTo { get; } = to;

    public abstract bool Up(IDbInterface db);
    public abstract bool Down(IDbInterface db);
}
