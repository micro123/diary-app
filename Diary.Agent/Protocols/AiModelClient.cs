using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Networking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.Agent.Protocols;

public sealed class AiModelClient : IAgentModelGateway, IAgentModelRequestAborter
{
    private readonly IAiCredentialStore _credentialStore;
    private readonly AiHttpClientPool _clientPool;
    private readonly IReadOnlyDictionary<AiProtocol, IAiProtocolAdapter> _adapters;
    private readonly ILogger<AiModelClient> _logger;

    public AiModelClient(
        IAiCredentialStore credentialStore,
        AiHttpClientPool clientPool,
        IEnumerable<IAiProtocolAdapter> adapters,
        ILogger<AiModelClient>? logger = null)
    {
        _credentialStore = credentialStore;
        _clientPool = clientPool;
        _adapters = adapters.ToDictionary(adapter => adapter.Protocol);
        _logger = logger ?? NullLogger<AiModelClient>.Instance;
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
        _logger.LogInformation(
            "开始模型请求。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, Endpoint={Endpoint}, Stream=false, Messages={MessageCount}, Tools={ToolCount}, TimeoutSeconds={TimeoutSeconds}",
            connection.Id,
            connection.Protocol,
            connection.Model,
            SafeEndpoint(message.RequestUri),
            request.Messages.Count,
            request.Tools.Count,
            connection.RequestTimeout.TotalSeconds);
        try
        {
            var client = await _clientPool.GetClientAsync(connection, timeout.Token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var parsed = await adapter.ParseResponseAsync(response, timeout.Token);
            _logger.LogInformation(
                "模型请求完成。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, StatusCode={StatusCode}, ToolCalls={ToolCalls}, HasText={HasText}",
                connection.Id,
                connection.Protocol,
                connection.Model,
                (int)response.StatusCode,
                parsed.ToolCalls.Count,
                !string.IsNullOrEmpty(parsed.Text));
            return parsed;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "模型请求超时。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}", connection.Id, connection.Protocol, connection.Model);
            throw new AiModelException(AiModelErrorCategory.Timeout, "request_timeout", "模型请求超时。", innerException: exception);
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogInformation("模型请求已取消。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}", connection.Id, connection.Protocol, connection.Model);
            throw new AiModelException(AiModelErrorCategory.Cancelled, "request_cancelled", "模型请求已取消。", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "模型网络请求失败。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, Endpoint={Endpoint}", connection.Id, connection.Protocol, connection.Model, SafeEndpoint(message.RequestUri));
            throw NormalizeNetworkException(exception);
        }
        catch (AiModelException exception)
        {
            _logger.LogWarning(exception, "模型协议响应失败。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, Category={Category}, Code={Code}, HttpStatus={HttpStatus}", connection.Id, connection.Protocol, connection.Model, exception.Category, exception.Code, exception.StatusCode);
            throw;
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
        var stream = StreamCoreAsync(message, connection, adapter, timeout.Token);
        _logger.LogInformation(
            "开始模型流式请求。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, Endpoint={Endpoint}, Messages={MessageCount}, Tools={ToolCount}, IdleTimeoutSeconds={TimeoutSeconds}",
            connection.Id,
            connection.Protocol,
            connection.Model,
            SafeEndpoint(message.RequestUri),
            request.Messages.Count,
            request.Tools.Count,
            connection.RequestTimeout.TotalSeconds);
        await using var enumerator = stream.GetAsyncEnumerator(timeout.Token);
        var eventCount = 0;
        while (true)
        {
            AgentStreamEvent current;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    _logger.LogInformation(
                        "模型流式请求结束。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, EventCount={EventCount}",
                        connection.Id,
                        connection.Protocol,
                        connection.Model,
                        eventCount);
                    yield break;
                }
                current = enumerator.Current;
                eventCount++;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "模型流式请求空闲超时。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, EventCount={EventCount}", connection.Id, connection.Protocol, connection.Model, eventCount);
                throw new AiModelException(
                    AiModelErrorCategory.Timeout,
                    "request_timeout",
                    "模型流式请求等待数据超时。",
                    innerException: exception);
            }
            catch (OperationCanceledException exception)
            {
                _logger.LogInformation("模型流式请求已取消。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, EventCount={EventCount}", connection.Id, connection.Protocol, connection.Model, eventCount);
                throw new AiModelException(
                    AiModelErrorCategory.Cancelled,
                    "request_cancelled",
                    "模型请求已取消。",
                    innerException: exception);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogError(exception, "模型流式网络请求失败。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, Endpoint={Endpoint}, EventCount={EventCount}", connection.Id, connection.Protocol, connection.Model, SafeEndpoint(message.RequestUri), eventCount);
                throw NormalizeNetworkException(exception);
            }
            catch (AiModelException exception)
            {
                _logger.LogWarning(exception, "模型流式协议响应失败。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, Category={Category}, Code={Code}, EventCount={EventCount}", connection.Id, connection.Protocol, connection.Model, exception.Category, exception.Code, eventCount);
                throw;
            }
            timeout.CancelAfter(connection.RequestTimeout);
            yield return current;
        }
    }

    public async ValueTask AbortConnectionAsync(
        AiConnectionProfile connection,
        CancellationToken cancellationToken = default)
    {
        var evicted = await _clientPool.EvictAsync(connection, cancellationToken);
        _logger.LogWarning(
            "已请求回收模型 HTTP 连接。ConnectionId={ConnectionId}, Protocol={Protocol}, Model={Model}, Evicted={Evicted}",
            connection.Id,
            connection.Protocol,
            connection.Model,
            evicted);
    }

    private async IAsyncEnumerable<AgentStreamEvent> StreamCoreAsync(
        HttpRequestMessage message,
        AiConnectionProfile connection,
        IAiProtocolAdapter adapter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            var client = await _clientPool.GetClientAsync(connection, cancellationToken);
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _ = await adapter.ParseResponseAsync(response, cancellationToken);
                yield break;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var item in adapter.ParseStreamAsync(stream, cancellationToken))
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

    internal static AiModelException NormalizeNetworkException(HttpRequestException exception)
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
        var authenticationException = FindInnerException<AuthenticationException>(exception);
        if (authenticationException is not null)
        {
            var revocationUnavailable = authenticationException.Message.Contains(
                                            "RevocationStatusUnknown",
                                            StringComparison.OrdinalIgnoreCase)
                                        || authenticationException.Message.Contains(
                                            "OfflineRevocation",
                                            StringComparison.OrdinalIgnoreCase);
            return new AiModelException(
                AiModelErrorCategory.Tls,
                revocationUnavailable ? "tls_revocation_offline" : "tls_error",
                revocationUnavailable
                    ? "证书吊销状态无法在线验证。请恢复 CRL/OCSP 网络，或在可信内网环境关闭“检查证书吊销状态”后重试。"
                    : "TLS 连接失败，请检查服务器证书、系统时间和证书信任链。",
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

    private static TException? FindInnerException<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException matched)
                return matched;
        }
        return null;
    }

    private static string SafeEndpoint(Uri? endpoint)
    {
        if (endpoint is null)
            return string.Empty;
        var sanitized = new UriBuilder(endpoint)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return sanitized.Uri.GetLeftPart(UriPartial.Path);
    }
}
