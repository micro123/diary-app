using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace Diary.Utils;

public class SingletonApp : IDisposable
{
    private bool _self;
    private readonly string _mutexKey;
    private readonly string _pipeKey;
    private readonly Mutex _mutex;
    private NamedPipeServerStream? _server;
    private readonly CancellationTokenSource? _token;

    public Action<string>? WakeupAction;

    public SingletonApp(string appId)
    {
        if (string.IsNullOrEmpty(appId))
            throw new ArgumentNullException(nameof(appId));

        _pipeKey = appId;
        _mutexKey = $@"Global\{appId}";
        _mutex = new Mutex(true, _mutexKey, out _self);
        if (_self)
        {
            // create pipe server
            _token = new CancellationTokenSource();
            Task.Run(() => ListenPipe(_token.Token));
        }
    }

    public bool IsSelfInstance()
    {
        return _self;
    }

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
            try
            {
                _server = new NamedPipeServerStream(_pipeKey, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await _server.WaitForConnectionAsync(token);

                if (_server.IsConnected)
                {
                    using var reader = new StreamReader(_server);
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
                _server?.Dispose();
                _server = null;
            }
        }
    }

    public void Dispose()
    {
        _token?.Cancel();
        _token?.Dispose();
        _server?.Dispose();
        _mutex?.Dispose();
    }
}
