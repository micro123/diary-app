using System.Net;
using System.Runtime.CompilerServices;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Networking;

namespace Diary.Agent.Protocols;

public sealed class AiModelClient : IAgentModelGateway
{
    private readonly IAiCredentialStore _credentialStore;
    private readonly AiHttpClientPool _clientPool;
    private readonly IReadOnlyDictionary<AiProtocol, IAiProtocolAdapter> _adapters;

    public AiModelClient(
        IAiCredentialStore credentialStore,
        AiHttpClientPool clientPool,
        IEnumerable<IAiProtocolAdapter> adapters)
    {
        _credentialStore = credentialStore;
        _clientPool = clientPool;
        _adapters = adapters.ToDictionary(adapter => adapter.Protocol);
    }

    public async ValueTask<AgentModelResponse> SendAsync(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CancellationToken cancellationToken = default)
    {
        if (request.Stream)
            throw new ArgumentException("SendAsync 只接受非流式请求。", nameof(request));
        var adapter = GetAdapter(connection.Protocol);
        var credential = await ResolveCredentialAsync(connection, cancellationToken);
        using var message = adapter.CreateRequest(request, connection, credential);
        await ApplyAdditionalHeadersAsync(message, connection, cancellationToken);
        using var timeout = CreateTimeout(connection.RequestTimeout, cancellationToken);
        try
        {
            var client = await _clientPool.GetClientAsync(connection, timeout.Token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
            return await adapter.ParseResponseAsync(response, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiModelException(AiModelErrorCategory.Timeout, "request_timeout", "模型请求超时。", innerException: exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new AiModelException(AiModelErrorCategory.Cancelled, "request_cancelled", "模型请求已取消。", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw NormalizeNetworkException(exception);
        }
    }

    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentModelRequest request,
        AiConnectionProfile connection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!request.Stream)
            throw new ArgumentException("StreamAsync 只接受流式请求。", nameof(request));
        var adapter = GetAdapter(connection.Protocol);
        var credential = await ResolveCredentialAsync(connection, cancellationToken);
        using var message = adapter.CreateRequest(request, connection, credential);
        await ApplyAdditionalHeadersAsync(message, connection, cancellationToken);
        using var timeout = CreateTimeout(connection.RequestTimeout, cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            var client = await _clientPool.GetClientAsync(connection, timeout.Token);
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _ = await adapter.ParseResponseAsync(response, timeout.Token);
                yield break;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await foreach (var item in adapter.ParseStreamAsync(stream, timeout.Token))
                yield return item;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private IAiProtocolAdapter GetAdapter(AiProtocol protocol) =>
        _adapters.TryGetValue(protocol, out var adapter)
            ? adapter
            : throw new AiModelException(
                AiModelErrorCategory.Configuration,
                "protocol_not_registered",
                $"协议适配器未注册：{protocol}。");

    private async ValueTask<CredentialValue?> ResolveCredentialAsync(
        AiConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        if (connection.Authentication.Kind == AiAuthenticationKind.None)
            return null;
        return await _credentialStore.GetAsync(connection.Authentication.CredentialReference, cancellationToken);
    }

    private static CancellationTokenSource CreateTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private async ValueTask ApplyAdditionalHeadersAsync(
        HttpRequestMessage request,
        AiConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        foreach (var header in connection.Headers)
        {
            var value = header.Value;
            if (!string.IsNullOrWhiteSpace(header.CredentialReference))
            {
                var credential = await _credentialStore.GetAsync(header.CredentialReference, cancellationToken)
                    ?? throw new AiModelException(
                        AiModelErrorCategory.Configuration,
                        "header_credential_missing",
                        $"额外 Header {header.Name} 的凭据不可用。");
                value = header.Prefix + credential.Reveal();
            }
            request.Headers.TryAddWithoutValidation(header.Name, value);
        }
    }

    private static AiModelException NormalizeNetworkException(HttpRequestException exception)
    {
        if (exception.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            return new AiModelException(
                AiModelErrorCategory.Proxy,
                "proxy_authentication_required",
                "代理服务器要求认证，请检查代理用户名和密码。",
                (int?)exception.StatusCode,
                exception);
        }
        return new AiModelException(
            AiModelErrorCategory.Network,
            "network_error",
            "无法连接模型服务。",
            (int?)exception.StatusCode,
            exception);
    }
}
