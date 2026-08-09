using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class ScriptApiFacade(IScriptExecutionContext context)
{
    public IDiaryApi Diary => context.GetRequiredApi<IDiaryApi>();
    public ITrackerApi Tracker => context.GetRequiredApi<ITrackerApi>();
    public SysApi System => context.GetRequiredApi<SysApi>();
    public ILogApi Log => context.GetRequiredApi<ILogApi>();
}

public static class ScriptContextApiExtensions
{
    public static ScriptApiFacade Api(this IScriptExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ScriptApiFacade(context);
    }
}
