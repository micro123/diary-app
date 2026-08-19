#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.UserScripts;

public sealed class OvertimeExportEditorScript : EditorScript
{
    public override string Id => "overtime-export";
    public override string Name => "导出加班明细";
    public override string? Description => "把当前右键范围内带有加班标签的工作项导出为 XLSX。";

    public override async ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptEditorContext context,
        CancellationToken cancellationToken = default)
    {
        var diary = context.GetApi<IDiaryApi>();
        var system = context.GetApi<SysApi>();
        var exports = context.GetApi<IExportApi>();
        if (diary is null || system is null || exports is null)
            return new(ScriptExecutionStatus.Rejected, []);

        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var item in QueryItemsAsync(context, diary, cancellationToken))
        {
            if (!HasOvertimeTag(item))
                continue;
            rows.Add([item.Date, item.Comment ?? string.Empty, item.Hours]);
        }

        if (rows.Count == 0)
        {
            await system.NotifyAsync("导出加班明细", "当前范围没有加班工作项。", cancellationToken);
            return ScriptExecutionResult.Succeeded();
        }

        var directory = await system.PickDirectoryAsync(
            new DirectoryPickerOptions { Title = "选择加班明细导出目录" },
            cancellationToken);
        if (directory is null)
            return ScriptExecutionResult.Succeeded();

        var result = await exports.ExportAsync(new ExportRequest
        {
            FormatId = "xlsx",
            DirectorySelectionId = directory.SelectionId,
            FileName = $"加班明细-{DateTime.Today:yyyyMMdd}.xlsx",
            FormatOptions = new ExportFormatOptions(
                "xlsx",
                new Dictionary<string, object?> { ["sheet_name"] = "加班明细" }),
            Content = new ExportTableContent
            {
                Title = "加班明细",
                Columns =
                [
                    new ExportColumn("日期", ExportColumnType.Date),
                    new ExportColumn("工作内容"),
                    new ExportColumn("工时", ExportColumnType.Decimal, "0.00"),
                ],
                Rows = rows,
                Aggregates = [new ExportAggregateColumn("工时", Label: "总工时")],
                Style = ExportTableStyle.Report,
            },
        }, cancellationToken);

        if (!result.Succeeded)
        {
            var error = result.Error!;
            await system.NotifyAsync(
                "导出失败",
                $"{error.Code}: {error.Message}\n可重试：{error.Retryable}",
                cancellationToken);
            return new(ScriptExecutionStatus.Failed, []);
        }

        await system.AskToOpenExportedFileAsync(result.FileId!, cancellationToken);
        return ScriptExecutionResult.Succeeded();
    }

    private static async IAsyncEnumerable<ScriptWorkItem> QueryItemsAsync(
        IScriptEditorContext context,
        IDiaryApi diary,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (context.GetDateRange() is not null)
        {
            await foreach (var item in context.StreamItemsAsync(cancellationToken))
                yield return item;
            yield break;
        }

        if (context.WorkItem is not { } workItem)
            yield break;
        await foreach (var item in diary.StreamAsync(
                           new ScriptWorkItemQuery
                           {
                               StartDate = workItem.Date,
                               EndDate = workItem.Date,
                           },
                           pageSize: 500,
                           cancellationToken))
            yield return item;
    }

    private static bool HasOvertimeTag(ScriptWorkItem item)
    {
        foreach (var tag in item.Tags)
        {
            if (string.Equals(tag.Name, "加班", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
