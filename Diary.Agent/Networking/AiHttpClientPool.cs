using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Networking;

public sealed class AiHttpClientPool(
    IAiCredentialStore credentialStore,
    Func<AiProxyConfiguration>? defaultProxyProvider = null) : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<HttpClient>>> _clients = new(StringComparer.Ordinal);
    private bool _disposed;

    public async ValueTask<HttpClient> GetClientAsync(
        AiConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var proxy = ResolveProxy(profile.Proxy);
        var proxyIdentity = await ResolveProxyIdentityAsync(proxy, cancellationToken);
        var fingerprint = CreateFingerprint(profile, proxyIdentity);
        var lazy = _clients.GetOrAdd(
            fingerprint,
            _ => new Lazy<Task<HttpClient>>(
                () => CreateClientAsync(profile, proxy, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            _clients.TryRemove(new KeyValuePair<string, Lazy<Task<HttpClient>>>(fingerprint, lazy));
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var lazy in _clients.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                lazy.Value.Result.Dispose();
        }
        _clients.Clear();
    }

    private async Task<HttpClient> CreateClientAsync(
        AiConnectionProfile profile,
        AiProxyConfiguration proxyConfiguration,
        CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = profile.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.Online,
            },
            AllowAutoRedirect = false,
        };
        switch (proxyConfiguration.Mode)
        {
            case AiProxyMode.Direct:
                handler.UseProxy = false;
                break;
            case AiProxyMode.Inherit:
            case AiProxyMode.System:
                handler.UseProxy = true;
                handler.Proxy = null;
                break;
            case AiProxyMode.Custom:
                handler.UseProxy = true;
                handler.Proxy = await CreateProxyAsync(proxyConfiguration, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(proxyConfiguration.Mode));
        }
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private async ValueTask<WebProxy> CreateProxyAsync(
        AiProxyConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var proxy = new WebProxy(configuration.Address!, false, configuration.BypassList.ToArray());
        var username = await ReadCredentialAsync(configuration.UsernameCredentialReference, cancellationToken);
        var password = await ReadCredentialAsync(configuration.PasswordCredentialReference, cancellationToken);
        if (username is not null || password is not null)
            proxy.Credentials = new NetworkCredential(username ?? string.Empty, password ?? string.Empty);
        return proxy;
    }

    private async ValueTask<string> ResolveProxyIdentityAsync(
        AiProxyConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (configuration.Mode != AiProxyMode.Custom)
            return configuration.Mode.ToString();
        var username = await ReadCredentialAsync(configuration.UsernameCredentialReference, cancellationToken);
        var password = await ReadCredentialAsync(configuration.PasswordCredentialReference, cancellationToken);
        var secretIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (username ?? string.Empty) + "\0" + (password ?? string.Empty))));
        return $"{configuration.Address}|{string.Join(';', configuration.BypassList)}|{secretIdentity}";
    }

    private async ValueTask<string?> ReadCredentialAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        return (await credentialStore.GetAsync(reference, cancellationToken))?.Reveal();
    }

    private AiProxyConfiguration ResolveProxy(AiProxyConfiguration profileProxy)
    {
        if (profileProxy.Mode != AiProxyMode.Inherit)
            return profileProxy;
        var inherited = defaultProxyProvider?.Invoke()
            ?? new AiProxyConfiguration { Mode = AiProxyMode.System };
        return inherited.Mode == AiProxyMode.Inherit
            ? new AiProxyConfiguration { Mode = AiProxyMode.System }
            : inherited;
    }

    private static string CreateFingerprint(AiConnectionProfile profile, string proxyIdentity) =>
        string.Join('|',
            profile.BaseUri.Scheme,
            profile.BaseUri.Host,
            profile.BaseUri.Port,
            profile.ConnectTimeout.Ticks,
            proxyIdentity);
}
