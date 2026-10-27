using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Diary.Agent.Configuration;

public sealed record AiConnectionShareExportResult(
    string Content,
    int ProfileCount,
    int StrippedCredentialCount);

public sealed record AiConnectionShareImportResult(
    IReadOnlyList<AiConnectionProfile> Profiles,
    int RenamedProfileCount,
    int RequiredCredentialCount);

public sealed class AiConnectionSharePackageService
{
    public const string FileExtension = ".diaryaiconnections";
    public const string FormatId = "diary-ai-connections";
    public const int FormatVersion = 2;
    public const int MaximumProfiles = 100;
    public const int MaximumPackageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public AiConnectionShareExportResult Export(IReadOnlyCollection<AiConnectionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
            throw new InvalidOperationException("至少选择一个要导出的模型连接。");
        if (profiles.Count > MaximumProfiles)
            throw new InvalidOperationException($"单个连接包最多包含 {MaximumProfiles} 个模型连接。");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var strippedCredentials = 0;
        var exported = new List<AiConnectionShareProfile>(profiles.Count);
        foreach (var profile in profiles)
        {
            var errors = AiConnectionProfileValidator.Validate(profile);
            if (errors.Count > 0)
                throw new InvalidOperationException($"连接 {profile.Id} 无效：{string.Join(" ", errors)}");
            if (!ids.Add(profile.Id))
                throw new InvalidOperationException($"连接 ID 重复：{profile.Id}。");

            var authenticationRequiresCredential = profile.Authentication.Kind != AiAuthenticationKind.None;
            if (authenticationRequiresCredential)
                strippedCredentials++;
            var proxyRequiresUsername = profile.Proxy.Mode == AiProxyMode.Custom
                                        && !string.IsNullOrWhiteSpace(profile.Proxy.UsernameCredentialReference);
            var proxyRequiresPassword = profile.Proxy.Mode == AiProxyMode.Custom
                                        && !string.IsNullOrWhiteSpace(profile.Proxy.PasswordCredentialReference);
            if (proxyRequiresUsername)
                strippedCredentials++;
            if (proxyRequiresPassword)
                strippedCredentials++;

            var headers = profile.Headers.Select(header =>
            {
                var requiresCredential = !string.IsNullOrWhiteSpace(header.CredentialReference);
                if (requiresCredential)
                    strippedCredentials++;
                return new AiConnectionShareHeader
                {
                    Name = header.Name,
                    Value = requiresCredential ? null : header.Value,
                    RequiresCredential = requiresCredential,
                    Prefix = header.Prefix,
                };
            }).ToList();

            exported.Add(new AiConnectionShareProfile
            {
                Id = profile.Id,
                DisplayName = profile.DisplayName,
                Protocol = profile.Protocol,
                BaseUrl = profile.BaseUri.AbsoluteUri,
                RequestPathOverride = profile.RequestPathOverride,
                Model = profile.Model,
                ImageInputMode = profile.ImageInputMode,
                Reasoning = new AiConnectionShareReasoning
                {
                    Effort = profile.Reasoning.Effort,
                    BudgetTokens = profile.Reasoning.BudgetTokens,
                    RequestSummary = profile.Reasoning.RequestSummary,
                },
                ContextWindowTokens = profile.ContextWindowTokens,
                AutomaticContextCompression = profile.AutomaticContextCompression,
                ContextCompressionThresholdPercent = profile.ContextCompressionThresholdPercent,
                Authentication = new AiConnectionShareAuthentication
                {
                    Kind = profile.Authentication.Kind,
                    HeaderName = profile.Authentication.HeaderName,
                    HeaderPrefix = profile.Authentication.HeaderPrefix,
                    RequiresCredential = authenticationRequiresCredential,
                },
                Proxy = new AiConnectionShareProxy
                {
                    Mode = profile.Proxy.Mode,
                    Address = profile.Proxy.Address?.AbsoluteUri,
                    BypassList = profile.Proxy.BypassList.ToList(),
                    RequiresUsernameCredential = proxyRequiresUsername,
                    RequiresPasswordCredential = proxyRequiresPassword,
                },
                ConnectTimeoutSeconds = (int)profile.ConnectTimeout.TotalSeconds,
                RequestTimeoutSeconds = (int)profile.RequestTimeout.TotalSeconds,
                CheckCertificateRevocation = profile.CheckCertificateRevocation,
                Compatibility = new AiConnectionShareCompatibility
                {
                    SendToolChoice = profile.Compatibility.SendToolChoice,
                    SendStreamOptions = profile.Compatibility.SendStreamOptions,
                    AnthropicVersion = profile.Compatibility.AnthropicVersion,
                },
                Headers = headers,
            });
        }

        var document = new AiConnectionShareDocument
        {
            Profiles = exported,
        };
        var content = JsonSerializer.Serialize(document, JsonOptions);
        if (Encoding.UTF8.GetByteCount(content) > MaximumPackageBytes)
            throw new InvalidOperationException("连接包超过 1 MiB 限制。");
        return new AiConnectionShareExportResult(content, exported.Count, strippedCredentials);
    }

    public AiConnectionShareImportResult Import(
        string content,
        IReadOnlyCollection<string> existingProfileIds)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(existingProfileIds);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("连接包内容为空。");
        if (Encoding.UTF8.GetByteCount(content) > MaximumPackageBytes)
            throw new InvalidDataException("连接包超过 1 MiB 限制。");

        AiConnectionShareDocument document;
        try
        {
            document = JsonSerializer.Deserialize<AiConnectionShareDocument>(content, JsonOptions)
                       ?? throw new InvalidDataException("连接包内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("连接包不是有效的 DiaryApp AI 连接 JSON。", exception);
        }

        ValidateDocument(document);
        var usedIds = existingProfileIds.ToHashSet(StringComparer.Ordinal);
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var profiles = new List<AiConnectionProfile>(document.Profiles.Count);
        var renamed = 0;
        var requiredCredentials = 0;
        foreach (var source in document.Profiles)
        {
            ValidateProfileShape(source);
            if (!sourceIds.Add(source.Id))
                throw new InvalidDataException($"连接包包含重复 ID：{source.Id}。");
            var id = CreateUniqueId(source.Id, usedIds);
            if (!string.Equals(id, source.Id, StringComparison.Ordinal))
                renamed++;
            usedIds.Add(id);

            if (!Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var baseUri))
                throw new InvalidDataException($"连接 {source.Id} 的 Base URL 无效。");
            Uri? proxyAddress = null;
            if (!string.IsNullOrWhiteSpace(source.Proxy.Address)
                && !Uri.TryCreate(source.Proxy.Address, UriKind.Absolute, out proxyAddress))
            {
                throw new InvalidDataException($"连接 {source.Id} 的代理地址无效。");
            }

            var authenticationReference = string.Empty;
            if (source.Authentication.Kind != AiAuthenticationKind.None)
            {
                if (!source.Authentication.RequiresCredential)
                    throw new InvalidDataException($"连接 {source.Id} 的认证配置缺少凭据需求标记。");
                authenticationReference = $"diary.ai/{id}/api-key";
                requiredCredentials++;
            }
            else if (source.Authentication.RequiresCredential)
            {
                throw new InvalidDataException($"连接 {source.Id} 使用无认证模式，不能声明认证凭据。");
            }

            var proxyUsernameReference = string.Empty;
            var proxyPasswordReference = string.Empty;
            if (source.Proxy.Mode == AiProxyMode.Custom)
            {
                if (source.Proxy.RequiresUsernameCredential)
                {
                    proxyUsernameReference = $"diary.ai/{id}/proxy-user";
                    requiredCredentials++;
                }
                if (source.Proxy.RequiresPasswordCredential)
                {
                    proxyPasswordReference = $"diary.ai/{id}/proxy-password";
                    requiredCredentials++;
                }
            }
            else if (source.Proxy.RequiresUsernameCredential || source.Proxy.RequiresPasswordCredential)
            {
                throw new InvalidDataException($"连接 {source.Id} 不是自定义代理，不能声明代理凭据。");
            }

            var headers = new List<AiRequestHeader>(source.Headers.Count);
            for (var index = 0; index < source.Headers.Count; index++)
            {
                var header = source.Headers[index];
                if (header.RequiresCredential)
                {
                    if (header.Value is not null)
                        throw new InvalidDataException($"连接 {source.Id} 的 Header {header.Name} 同时包含普通值和凭据标记。");
                    headers.Add(new AiRequestHeader
                    {
                        Name = header.Name,
                        CredentialReference = $"diary.ai/{id}/header-{index + 1}",
                        Prefix = header.Prefix,
                    });
                    requiredCredentials++;
                }
                else
                {
                    if (header.Value is null)
                        throw new InvalidDataException($"连接 {source.Id} 的 Header {header.Name} 缺少普通值。");
                    headers.Add(new AiRequestHeader
                    {
                        Name = header.Name,
                        Value = header.Value,
                        Prefix = header.Prefix,
                    });
                }
            }

            var profile = new AiConnectionProfile
            {
                Id = id,
                DisplayName = source.DisplayName,
                Protocol = source.Protocol,
                BaseUri = baseUri,
                RequestPathOverride = source.RequestPathOverride,
                Model = source.Model,
                ImageInputMode = source.ImageInputMode,
                Reasoning = new AiReasoningOptions
                {
                    Effort = source.Reasoning.Effort,
                    BudgetTokens = source.Reasoning.BudgetTokens,
                    RequestSummary = source.Reasoning.RequestSummary,
                },
                ContextWindowTokens = source.ContextWindowTokens,
                AutomaticContextCompression = source.AutomaticContextCompression,
                ContextCompressionThresholdPercent = source.ContextCompressionThresholdPercent,
                Authentication = new AiAuthenticationConfiguration
                {
                    Kind = source.Authentication.Kind,
                    CredentialReference = authenticationReference,
                    HeaderName = source.Authentication.HeaderName,
                    HeaderPrefix = source.Authentication.HeaderPrefix,
                },
                Proxy = new AiProxyConfiguration
                {
                    Mode = source.Proxy.Mode,
                    Address = proxyAddress,
                    BypassList = source.Proxy.BypassList.ToArray(),
                    UsernameCredentialReference = proxyUsernameReference,
                    PasswordCredentialReference = proxyPasswordReference,
                },
                ConnectTimeout = TimeSpan.FromSeconds(source.ConnectTimeoutSeconds),
                RequestTimeout = TimeSpan.FromSeconds(source.RequestTimeoutSeconds),
                CheckCertificateRevocation = source.CheckCertificateRevocation,
                Compatibility = new AiCompatibilityOptions
                {
                    SendToolChoice = source.Compatibility.SendToolChoice,
                    SendStreamOptions = source.Compatibility.SendStreamOptions,
                    AnthropicVersion = source.Compatibility.AnthropicVersion,
                },
                Headers = headers,
            };
            var errors = AiConnectionProfileValidator.Validate(profile);
            if (errors.Count > 0)
                throw new InvalidDataException($"连接 {source.Id} 无效：{string.Join(" ", errors)}");
            profiles.Add(profile);
        }

        return new AiConnectionShareImportResult(profiles, renamed, requiredCredentials);
    }

    private static void ValidateDocument(AiConnectionShareDocument document)
    {
        if (!string.Equals(document.Format, FormatId, StringComparison.Ordinal))
            throw new InvalidDataException("文件不是 DiaryApp AI 连接包。");
        if (document.Version is < 1 or > FormatVersion)
            throw new InvalidDataException($"不支持的 AI 连接包版本：{document.Version}。");
        if (document.Profiles is null || document.Profiles.Count == 0)
            throw new InvalidDataException("连接包不包含模型连接。");
        if (document.Profiles.Count > MaximumProfiles)
            throw new InvalidDataException($"连接包最多允许 {MaximumProfiles} 个模型连接。");
    }

    private static void ValidateProfileShape(AiConnectionShareProfile source)
    {
        if (source is null)
            throw new InvalidDataException("连接包不能包含空连接。");
        if (source.Id is null || source.DisplayName is null || source.BaseUrl is null || source.Model is null)
            throw new InvalidDataException("连接包中的连接缺少必需文本字段。");
        if (source.Authentication is null || source.Proxy is null || source.Compatibility is null || source.Reasoning is null)
            throw new InvalidDataException($"连接 {source.Id} 缺少认证、代理或兼容配置。");
        if (source.Authentication.HeaderName is null || source.Authentication.HeaderPrefix is null)
            throw new InvalidDataException($"连接 {source.Id} 的认证 Header 配置无效。");
        if (source.Proxy.BypassList is null || source.Proxy.BypassList.Any(item => item is null))
            throw new InvalidDataException($"连接 {source.Id} 的代理绕过列表无效。");
        if (source.Headers is null || source.Headers.Any(header => header is null))
            throw new InvalidDataException($"连接 {source.Id} 的额外 Header 列表无效。");
        if (source.Headers.Any(header => header.Name is null || header.Prefix is null))
            throw new InvalidDataException($"连接 {source.Id} 的额外 Header 配置无效。");
        if (string.IsNullOrWhiteSpace(source.Compatibility.AnthropicVersion)
            || source.Compatibility.AnthropicVersion.Contains('\r')
            || source.Compatibility.AnthropicVersion.Contains('\n'))
        {
            throw new InvalidDataException($"连接 {source.Id} 的 Anthropic 版本配置无效。");
        }
    }

    private static string CreateUniqueId(string sourceId, IReadOnlySet<string> usedIds)
    {
        if (!usedIds.Contains(sourceId))
            return sourceId;
        for (var index = 1; index <= MaximumProfiles + usedIds.Count; index++)
        {
            var suffix = index == 1 ? "-imported" : $"-imported-{index}";
            var prefixLength = Math.Min(sourceId.Length, 100 - suffix.Length);
            var candidate = sourceId[..prefixLength].TrimEnd('.', '_', '-') + suffix;
            if (!usedIds.Contains(candidate))
                return candidate;
        }
        throw new InvalidOperationException($"无法为导入连接 {sourceId} 生成唯一 ID。");
    }

    private sealed class AiConnectionShareDocument
    {
        [JsonRequired]
        public string Format { get; set; } = FormatId;

        [JsonRequired]
        public int Version { get; set; } = FormatVersion;

        [JsonRequired]
        public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        [JsonRequired]
        public List<AiConnectionShareProfile> Profiles { get; set; } = [];
    }

    private sealed class AiConnectionShareProfile
    {
        [JsonRequired]
        public string Id { get; set; } = string.Empty;

        [JsonRequired]
        public string DisplayName { get; set; } = string.Empty;

        [JsonRequired]
        public AiProtocol Protocol { get; set; }

        [JsonRequired]
        public string BaseUrl { get; set; } = string.Empty;

        public string? RequestPathOverride { get; set; }

        [JsonRequired]
        public string Model { get; set; } = string.Empty;

        public AiImageInputMode ImageInputMode { get; set; } = AiImageInputMode.Auto;

        public AiConnectionShareReasoning Reasoning { get; set; } = new();

        [JsonRequired]
        public int ContextWindowTokens { get; set; } = AiConnectionProfile.DefaultContextWindowTokens;

        [JsonRequired]
        public bool AutomaticContextCompression { get; set; } = true;

        [JsonRequired]
        public int ContextCompressionThresholdPercent { get; set; } = 75;

        [JsonRequired]
        public AiConnectionShareAuthentication Authentication { get; set; } = new();

        [JsonRequired]
        public AiConnectionShareProxy Proxy { get; set; } = new();

        [JsonRequired]
        public int ConnectTimeoutSeconds { get; set; } = 30;

        [JsonRequired]
        public int RequestTimeoutSeconds { get; set; } = 120;

        [JsonRequired]
        public bool CheckCertificateRevocation { get; set; } = true;

        [JsonRequired]
        public AiConnectionShareCompatibility Compatibility { get; set; } = new();

        [JsonRequired]
        public List<AiConnectionShareHeader> Headers { get; set; } = [];
    }

    private sealed class AiConnectionShareReasoning
    {
        public AiReasoningEffort Effort { get; set; } = AiReasoningEffort.ProviderDefault;

        public int? BudgetTokens { get; set; }

        public bool RequestSummary { get; set; } = true;
    }

    private sealed class AiConnectionShareAuthentication
    {
        [JsonRequired]
        public AiAuthenticationKind Kind { get; set; } = AiAuthenticationKind.Bearer;

        [JsonRequired]
        public string HeaderName { get; set; } = "Authorization";

        [JsonRequired]
        public string HeaderPrefix { get; set; } = "Bearer ";

        [JsonRequired]
        public bool RequiresCredential { get; set; } = true;
    }

    private sealed class AiConnectionShareProxy
    {
        [JsonRequired]
        public AiProxyMode Mode { get; set; } = AiProxyMode.System;

        public string? Address { get; set; }

        [JsonRequired]
        public List<string> BypassList { get; set; } = [];

        [JsonRequired]
        public bool RequiresUsernameCredential { get; set; }

        [JsonRequired]
        public bool RequiresPasswordCredential { get; set; }
    }

    private sealed class AiConnectionShareHeader
    {
        [JsonRequired]
        public string Name { get; set; } = string.Empty;

        public string? Value { get; set; }

        [JsonRequired]
        public bool RequiresCredential { get; set; }

        [JsonRequired]
        public string Prefix { get; set; } = string.Empty;
    }

    private sealed class AiConnectionShareCompatibility
    {
        [JsonRequired]
        public bool SendToolChoice { get; set; } = true;

        [JsonRequired]
        public bool SendStreamOptions { get; set; } = true;

        [JsonRequired]
        public string AnthropicVersion { get; set; } = "2023-06-01";
    }
}
