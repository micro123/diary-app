using System.ComponentModel;
using System.Text.Json;
using Diary.AiContext;
using ModelContextProtocol.Server;

namespace Diary.Mcp;

[McpServerToolType]
public sealed class DiaryScriptTools
{
    [McpServerTool(Name = "diary_validate_script", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("只编译或解析给定的 DiaryApp C#、Lua、Python 脚本并返回诊断；不读取文件、不加载编译产物，也不执行脚本。")]
    public static async ValueTask<string> ValidateScriptAsync(
        ScriptValidationService validationService,
        [Description("脚本语言：csharp、lua 或 python。")]
        string language,
        [Description("需要校验的完整脚本源码；最大 256 KiB。")]
        string source,
        CancellationToken cancellationToken = default)
    {
        var result = await validationService.ValidateAsync(language, source, cancellationToken);
        return JsonSerializer.Serialize(result, AiContextSerializer.JsonOptions);
    }
}
