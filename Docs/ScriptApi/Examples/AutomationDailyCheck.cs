#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.UserScripts;

public sealed class AutomationDailyCheckScript : AutomationScript
{
    public override string Id => "automation-daily-check";
    public override string Name => "每日自查补录";
    public override string? Description => "每天定时检查昨天是否有工作记录，没有则补录一条并提醒。";

    public override async ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptAutomationContext context,
        CancellationToken cancellationToken = default)
    {
        var diary = context.GetApi<IDiaryApi>();
        var system = context.GetApi<SysApi>();
        var log = context.GetApi<ILogApi>();
        if (diary is null || system is null)
            return new(ScriptExecutionStatus.Rejected, []);

        var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        var dateText = yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (log is not null)
            await log.InfoAsync($"自动化触发：{context.Automation.Trigger}，检查日期 {dateText}", cancellationToken);

        var query = await diary.QueryAsync(new ScriptWorkItemQuery
        {
            StartDate = dateText,
            EndDate = dateText,
            Limit = 1,
        }, cancellationToken);
        if (query.Succeeded && query.Items.Length > 0)
            return ScriptExecutionResult.Succeeded();

        var result = await diary.CreateLogItemAsync(new ScriptLogItemRequest(
            Date: dateText,
            Hours: 0.5,
            Title: "昨日无记录自动补录",
            Note: "自动化脚本补录，请修改为实际工作内容。",
            IdempotencyKey: $"auto-daily-check:{dateText}"), cancellationToken);
        if (!result.Succeeded)
            return new(ScriptExecutionStatus.Failed, []);

        await system.NotifyAsync(
            "自动化脚本",
            $"昨天（{dateText}）没有工作记录，已自动补录一条，请核对并修改。",
            cancellationToken);
        return new(ScriptExecutionStatus.Succeeded, [], result.Effects);
    }
}
