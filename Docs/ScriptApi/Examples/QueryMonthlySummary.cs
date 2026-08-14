#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.UserScripts;

public sealed class QueryMonthlySummaryScript : QueryScript
{
    public override string Id => "query-monthly-summary";
    public override string Name => "本月工时汇总";
    public override string? Description => "按主标签汇总本月工作项工时，输出到脚本日志。";

    public override async ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default)
    {
        var diary = context.GetApi<IDiaryApi>();
        var log = context.GetApi<ILogApi>();
        if (diary is null || log is null)
            return new(ScriptExecutionStatus.Rejected, []);

        var totals = new Dictionary<string, double>();
        await foreach (var item in diary.StreamAsync(
                           new ScriptWorkItemQuery { Range = "thisMonth" },
                           pageSize: 500,
                           cancellationToken))
        {
            var tagName = GetPrimaryTagName(item);
            totals[tagName] = totals.TryGetValue(tagName, out var hours) ? hours + item.Hours : item.Hours;
        }

        var lines = new List<string> { "主标签 | 工时" };
        foreach (var pair in totals.OrderByDescending(pair => pair.Value))
            lines.Add($"{pair.Key} | {pair.Value.ToString("0.##", CultureInfo.InvariantCulture)} 小时");

        await log.InfoAsync(string.Join("\n", lines), cancellationToken);
        return ScriptExecutionResult.Succeeded();
    }

    private static string GetPrimaryTagName(ScriptWorkItem item)
    {
        foreach (var tag in item.Tags)
        {
            if (tag.IsPrimary)
                return tag.Name;
        }

        return "无";
    }
}
