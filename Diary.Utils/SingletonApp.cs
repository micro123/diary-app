using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace Diary.Utils;

/// <summary>
/// 跨平台单实例守卫。
/// 判据使用独占文件锁（<see cref="FileShare.None"/>，进程退出/崩溃即自动释放，
/// Windows/Linux/macOS 均可靠）；唤起通知使用命名管道（第一个实例监听，后续实例连接后发消息再退出）。
/// </summary>
/// <remarks>
/// 不使用命名 <see cref="Mutex"/>：.NET 命名 Mutex 在 Linux/macOS 上是进程内的，不跨进程，
/// 无法在非 Windows 上实现单例。文件锁在所有平台上都是跨进程可靠的。
/// </remarks>
public class SingletonApp : IDisposable
{
    private readonly string _pipeKey;
    private readonly string _lockPath;
    private FileStream? _lockStream;
    private NamedPipeServerStream? _server;
    private CancellationTokenSource? _token;
    private Task? _listenTask;
    private bool _self;

    /// <summary>
    /// 收到后续实例的唤起消息时触发。在后台监听线程上调用，
    /// 调用方负责切回 UI 线程（例如包 <c>Dispatcher.UIThread.Post</c>）。
    /// </summary>
    public Action<string>? WakeupAction;

    public SingletonApp(string appId)
    {
        if (string.IsNullOrEmpty(appId))
            throw new ArgumentNullException(nameof(appId));

        // 文件名/管道名安全化
        var safe = new StringBuilder(appId.Length);
        foreach (var c in appId)
            safe.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        _pipeKey = safe.ToString();
        _lockPath = Path.Combine(FsTools.GetApplicationDataDirectory(), $"{_pipeKey}.lock");

        // 独占文件锁作为跨平台单例判据
        try
        {
            _lockStream = new FileStream(_lockPath, FileMode.Create, FileAccess.Write, FileShare.None);
            _self = true;
        }
        catch (IOException)
        {
            // 已有实例持有锁
            _self = false;
            return;
        }

        // 第一个实例：启动命名管道监听，接收后续实例的唤起消息
        _token = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenPipe(_token.Token));
    }

    public bool IsSelfInstance() => _self;

    /// <summary>
    /// 向已运行实例发送唤起消息（由后续实例调用）。
    /// </summary>
    public void Notify(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeKey, PipeDirection.Out);
            client.Connect(3000);

            using var writer = new StreamWriter(client);
            writer.Write(message);
            writer.Flush();
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("Pipe connection timed out!");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Write pipe error {ex.Message}");
        }
    }

    private async Task ListenPipe(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(_pipeKey, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _server = server;
                await server.WaitForConnectionAsync(token);

                if (server.IsConnected)
                {
                    using var reader = new StreamReader(server);
                    var msg = await reader.ReadToEndAsync();

                    WakeupAction?.Invoke(msg);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"管道出错：{e.Message}");
            }
            finally
            {
                server?.Dispose();
                if (ReferenceEquals(_server, server))
                    _server = null;
            }
        }
    }

    public void Dispose()
    {
        // 先取消并等待监听任务退出，再释放 token/server/锁，避免释放竞态
        _token?.Cancel();
        if (_listenTask is not null)
        {
            try { _listenTask.Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException) { }
        }

        _server?.Dispose();
        _server = null;
        _token?.Dispose();
        _token = null;
        _lockStream?.Dispose();
        _lockStream = null;
    }
}
