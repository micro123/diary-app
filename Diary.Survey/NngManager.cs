using System.Reflection;
using nng;

namespace Diary.Survey;

internal static class NngManager
{
    internal static IAPIFactory<INngMsg> Factory { get; private set; }
    internal const ushort ListenPort = SurveyPorts.Legacy;

    internal static string GetListenAddress(ushort port) => $"tcp://*:{port}";

    static NngManager()
    {
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly()!.Location);
        var ctx = new NngLoadContext(assemblyDir);
        Factory = NngLoadContext.Init(ctx);
    }
}
