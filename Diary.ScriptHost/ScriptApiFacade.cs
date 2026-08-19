using Diary.ScriptBase;

namespace Diary.ScriptHost;

/// <summary>
/// C# 脚本的推荐 API 入口。通过 <see cref="ScriptContextApiExtensions.Api"/> 获取。
/// </summary>
public sealed class ScriptApiFacade(IScriptExecutionContext context)
{
    public IDiaryApi Diary => context.GetRequiredApi<IDiaryApi>();
    public ITrackerApi Tracker => context.GetRequiredApi<ITrackerApi>();
    public SysApi System => context.GetRequiredApi<SysApi>();
    public IExportApi Exports => context.GetRequiredApi<IExportApi>();
    public ILogApi Log => context.GetRequiredApi<ILogApi>();
}

public static class ScriptContextApiExtensions
{
    /// <summary>获取按业务域组织的 C# 脚本 API 门面。</summary>
    public static ScriptApiFacade Api(this IScriptExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ScriptApiFacade(context);
    }
}
