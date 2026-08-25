#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.UserScripts;

public sealed class ParameterizedSummaryScript : QueryScriptV2
{
    public override string Id => "parameterized-summary-csharp";
    public override string Name => "参数化工时汇总（C#）";
    public override string? Description => "演示加载期参数声明、默认值和规范化参数读取。";

    public override IReadOnlyList<ScriptParameterDefinition> Parameters =>
    [
        new(
            "range",
            "统计范围",
            ScriptParameterType.Choice,
            Required: true,
            DefaultValue: "thisWeek",
            Choices:
            [
                new("thisWeek", "本周"),
                new("thisMonth", "本月"),
            ]),
        new(
            "minimumHours",
            "最低工时",
            ScriptParameterType.Number,
            DefaultValue: "0",
            Constraints: new(Minimum: "0", Maximum: "24", Step: "0.5", Unit: "小时")),
        new(
            "includeZero",
            "包含零工时事项",
            ScriptParameterType.Boolean,
            DefaultValue: "false"),
        new(
            "titlePrefix",
            "标题前缀",
            ScriptParameterType.String,
            DefaultValue: "工时汇总",
            Constraints: new(
                MaxLength: 20,
                Suggestions:
                [
                    new("工时汇总", "工时汇总"),
                    new("团队周报", "团队周报"),
                ])),
    ];

    public override async ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default)
    {
        var api = context.Api();
        var range = context.Arguments["range"];
        var minimumHours = double.Parse(context.Arguments["minimumHours"], CultureInfo.InvariantCulture);
        var includeZero = context.Arguments["includeZero"] == "true";
        var titlePrefix = context.Arguments["titlePrefix"];
        var count = 0;
        var totalHours = 0d;

        await foreach (var item in api.Diary.StreamAsync(
                           new ScriptWorkItemQuery { Range = range },
                           pageSize: 500,
                           cancellationToken))
        {
            if (item.Hours < minimumHours || (!includeZero && item.Hours == 0))
                continue;
            count++;
            totalHours += item.Hours;
        }

        await api.Log.InfoAsync(
            $"{titlePrefix}：范围 {range}；事项 {count}；工时 {totalHours.ToString("0.##", CultureInfo.InvariantCulture)}",
            cancellationToken);
        return ScriptExecutionResult.Succeeded();
    }
}
