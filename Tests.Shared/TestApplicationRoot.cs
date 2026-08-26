using System.Reflection;
using System.Runtime.CompilerServices;
using Diary.Utils;

internal static class TestApplicationRoot
{
    private const string RootEnvironmentVariable = "DIARY_TEST_APPLICATION_ROOT";

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootEnvironmentVariable)))
            return;

        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "Diary.Tests";
        var root = Path.Combine(
            Path.GetTempPath(),
            "DiaryApp.Tests",
            assemblyName,
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(RootEnvironmentVariable, root);
        FsTools.SetApplicationRootForCurrentProcess(root);
    }
}
