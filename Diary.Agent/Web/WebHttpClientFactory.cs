using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Web;

public sealed class WebHttpClientFactory(
    WebAccessPolicy policy,
    WebTargetValidator validator,
    IAiCredentialStore credentials) : IDisposable
{
    private readonly Lazy<Task<HttpClient>> _client = new(
        () => CreateClientAsync(policy, validator, credentials),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public ValueTask<HttpClient> GetClientAsync() => new(_client.Value);

    public void Dispose()
    {
        if (_client.IsValueCreated && _client.Value.IsCompletedSuccessfully)
            _client.Value.Result.Dispose();
    }

    private static async Task<HttpClient> CreateClientAsync(
        WebAccessPolicy policy,
        WebTargetValidator validator,
        IAiCredentialStore credentials)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(policy.Timeout.TotalSeconds, 30)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 4,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.Online,
            },
        };
        switch (policy.Proxy.Mode)
        {
            case AiProxyMode.Direct:
                handler.UseProxy = false;
                handler.ConnectCallback = async (context, cancellationToken) =>
                {
                    var scheme = context.InitialRequestMessage.RequestUri?.Scheme
                        ?? (context.DnsEndPoint.Port == 443 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp);
                    var target = await validator.ValidateAsync(
                        new UriBuilder(scheme, context.DnsEndPoint.Host, context.DnsEndPoint.Port).Uri,
                        cancellationToken);
                    Exception? lastError = null;
                    foreach (var address in target.Addresses)
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                        {
                            socket.Dispose();
                            lastError = exception;
                            if (exception is OperationCanceledException)
                                throw;
                        }
                    }
                    throw new WebFetchException(WebFetchErrorCode.Network, "无法连接已批准的网页目标地址。", lastError);
                };
                break;
            case AiProxyMode.Inherit:
            case AiProxyMode.System:
                handler.UseProxy = true;
                break;
            case AiProxyMode.Custom:
                handler.UseProxy = true;
                var proxy = new WebProxy(policy.Proxy.Address!, false, policy.Proxy.BypassList.ToArray());
                var username = await ReadCredentialAsync(credentials, policy.Proxy.UsernameCredentialReference);
                var password = await ReadCredentialAsync(credentials, policy.Proxy.PasswordCredentialReference);
                if (username is not null || password is not null)
                    proxy.Credentials = new NetworkCredential(username ?? string.Empty, password ?? string.Empty);
                handler.Proxy = proxy;
                break;
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<string?> ReadCredentialAsync(
        IAiCredentialStore store,
        string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        return (await store.GetAsync(reference))?.Reveal();
    }
}
