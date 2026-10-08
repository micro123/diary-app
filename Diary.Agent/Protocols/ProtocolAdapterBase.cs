using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Protocols;

public abstract class ProtocolAdapterBase : IAiProtocolAdapter
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public abstract AiProtocol Protocol { get; }

    public abstract string DefaultRequestPath { get; }

    public abstract HttpRequestMessage CreateRequest(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CredentialValue? credential);

    public abstract ValueTask<AgentModelResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken);

    public abstract IAsyncEnumerable<AgentStreamEvent> ParseStreamAsync(
        Stream stream,
        CancellationToken cancellationToken);

    protected static HttpRequestMessage CreateJsonRequest(
        JsonObject body,
        AiConnectionProfile connection,
        CredentialValue? credential,
        string defaultPath)
    {
        var uri = AiConnectionProfileValidator.ResolveRequestUri(connection, defaultPath);
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
        };
        ApplyAuthentication(request, connection.Authentication, credential);
        return request;
    }

    protected static void ApplyAuthentication(
        HttpRequestMessage request,
        AiAuthenticationConfiguration authentication,
        CredentialValue? credential)
    {
        if (authentication.Kind == AiAuthenticationKind.None)
            return;
        if (credential is null)
            throw new AiModelException(
                AiModelErrorCategory.Configuration,
                "credential_missing",
                "连接所需凭据不存在。");
        request.Headers.TryAddWithoutValidation(
            authentication.HeaderName,
            authentication.HeaderPrefix + credential.Reveal());
    }

    protected static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(response, cancellationToken);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new AiModelException(
                AiModelErrorCategory.Protocol,
                "invalid_json",
                "模型服务返回了无效 JSON。",
                (int)response.StatusCode,
                exception);
        }
    }

    protected static async ValueTask<AiModelException> CreateHttpErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var category = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AiModelErrorCategory.Authentication,
            HttpStatusCode.NotFound => AiModelErrorCategory.NotFound,
            HttpStatusCode.ProxyAuthenticationRequired => AiModelErrorCategory.Proxy,
            HttpStatusCode.TooManyRequests => AiModelErrorCategory.RateLimit,
            >= HttpStatusCode.InternalServerError => AiModelErrorCategory.Service,
            _ => AiModelErrorCategory.Protocol,
        };
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var safeMessage = TryReadErrorMessage(body) ?? $"模型服务返回 HTTP {(int)response.StatusCode}。";
        return new AiModelException(category, "http_error", safeMessage, (int)response.StatusCode);
    }

    protected static AgentUsage? ReadUsage(JsonElement usage, string inputName, string outputName, string totalName)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return null;
        return new AgentUsage(
            TryGetInt64(usage, inputName),
            TryGetInt64(usage, outputName),
            TryGetInt64(usage, totalName));
    }

    protected static long? TryGetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;

    protected static JsonElement ParseArguments(string? arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new AiModelException(
                AiModelErrorCategory.Protocol,
                "invalid_tool_arguments",
                "模型返回的工具参数不是有效 JSON。",
                innerException: exception);
        }
    }

    protected static JsonObject ToFunctionTool(AgentToolDefinition tool, string schemaPropertyName = "parameters") =>
        new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                [schemaPropertyName] = JsonNode.Parse(tool.InputSchema.GetRawText()),
            },
        };

    private static string? TryReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024)
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.TryGetProperty("message", out var message))
                    return message.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
