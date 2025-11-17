namespace Diary.Database;

public abstract class Migration(uint from, uint to)
{
    /// <summary>
    /// 从哪个版本升级上来，升级过程会写入新的版本
    /// </summary>
    public uint VersionFrom { get; } = from;
    
    /// <summary>
    /// 应用此升级会数据版本会是这个值
    /// </summary>
    public uint VersionTo { get; } = to;

    /// <summary>
    /// 如何升级
    /// </summary>
    /// <param name="db">要执行升级的数据库</param>
    /// <returns>是否升级成功</returns>
    public abstract bool Up(DbInterfaceBase db);
}
