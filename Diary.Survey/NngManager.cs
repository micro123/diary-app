using System.Reflection;
using nng;

namespace Diary.Survey;

internal static class NngManager
{
    internal static IAPIFactory<INngMsg> Factory { get; private set; }
    internal const string ListenAddress = "tcp://*:9721";
    internal const ushort ListenPort = 9721;

    static NngManager()
    {
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly()!.Location);
        var ctx = new NngLoadContext(assemblyDir);
        Factory = NngLoadContext.Init(ctx);
    }
}