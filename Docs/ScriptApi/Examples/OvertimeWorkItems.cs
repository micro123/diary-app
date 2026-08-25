#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.UserScripts;

public sealed class OvertimeWorkItemsEditorScript : EditorScript
{
    public override string Id => "overtime-work-items";
    public override string Name => "查询加班工作项";
    public override string? Description => "从右键菜单列出当前范围内带有加班标签的工作项。";

    public override async ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptEditorContext context,
        CancellationToken cancellationToken = default)
    {
        var system = context.GetApi<ISysApi>();
        if (system is null)
            return new(ScriptExecutionStatus.Rejected, []);

        var matchedItems = new List<ScriptWorkItem>();
        if (context.GetDateRange() is not null)
        {
            await foreach (var item in context.StreamItemsAsync(cancellationToken))
            {
                if (HasOvertimeTag(item))
                    matchedItems.Add(item);
            }
        }
        else if (context.WorkItem is { } workItem)
        {
            var diary = context.GetApi<IDiaryApi>();
            if (diary is null)
                return new(ScriptExecutionStatus.Rejected, []);

            await foreach (var item in diary.StreamAsync(
                               new ScriptWorkItemQuery
                               {
                                   StartDate = workItem.Date,
                                   EndDate = workItem.Date,
                               },
                               pageSize: 500,
                               cancellationToken))
            {
                if (HasOvertimeTag(item))
                    matchedItems.Add(item);
            }
        }
        else
        {
            return new(ScriptExecutionStatus.Rejected, []);
        }

        var message = matchedItems.Count == 0
            ? "无"
            : FormatItems(matchedItems);
        await system.NotifyAsync("加班工作项", message, cancellationToken);
        return ScriptExecutionResult.Succeeded();
    }

    private static bool HasOvertimeTag(ScriptWorkItem item)
    {
        foreach (var tag in item.Tags)
        {
            if (tag.Name == "加班")
                return true;
        }

        return false;
    }

    private static string FormatItems(IReadOnlyList<ScriptWorkItem> items)
    {
        var builder = new StringBuilder("日期 | 标题 | 主标签 | 工时");
        foreach (var item in items)
        {
            builder.Append('\n');
            builder.Append(item.Date);
            builder.Append(" | ");
            builder.Append(string.IsNullOrWhiteSpace(item.Comment) ? "（无标题）" : item.Comment);
            builder.Append(" | ");
            builder.Append(GetPrimaryTagName(item));
            builder.Append(" | ");
            builder.Append(item.Hours.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(" 小时");
        }

        return builder.ToString();
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
