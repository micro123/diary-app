using Diary.AiContext;
using Diary.Mcp;
using Diary.Script.CSharp;
using Diary.Script.Lua;
using Diary.Script.Py;
using Diary.ScriptBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

AiContextSnapshot snapshot;
try
{
    var snapshotPath = GetSnapshotPath(args);
    snapshot = await AiContextSerializer.LoadAsync(snapshotPath);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
    or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
{
    await Console.Error.WriteLineAsync($"无法加载 DiaryApp AI 上下文快照：{exception.Message}");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(snapshot);
builder.Services.AddSingleton<AiContextQueryService>();
builder.Services.AddSingleton<CSharpEngine>();
builder.Services.AddSingleton<LuaEngine>();
builder.Services.AddSingleton<PythonRuntimeResolver>();
builder.Services.AddSingleton<PythonEngine>();
builder.Services.AddSingleton<IScriptValidatorV1>(services => services.GetRequiredService<CSharpEngine>());
builder.Services.AddSingleton<IScriptValidatorV1>(services => services.GetRequiredService<LuaEngine>());
builder.Services.AddSingleton<IScriptValidatorV1>(services => services.GetRequiredService<PythonEngine>());
builder.Services.AddSingleton<ScriptValidationService>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DiaryContextTools>()
    .WithTools<DiaryScriptTools>();
await builder.Build().RunAsync();
return 0;

static string GetSnapshotPath(string[] arguments)
{
    for (var index = 0; index < arguments.Length; index++)
    {
        if (arguments[index] == "--snapshot" && index + 1 < arguments.Length
            && !string.IsNullOrWhiteSpace(arguments[index + 1]))
            return Path.GetFullPath(arguments[index + 1]);
    }
    throw new ArgumentException("必须通过 --snapshot <path> 指定只读 AI 上下文快照。");
}
