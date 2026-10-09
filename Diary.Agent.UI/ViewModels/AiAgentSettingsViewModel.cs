using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Web;
using Diary.GUIBase.ViewModels;

namespace Diary.Agent.UI.ViewModels;

public sealed partial class AiConnectionEditorViewModel : ObservableObject
{
    public AiConnectionEditorViewModel(AiConnectionProfile profile, AiConnectionCapabilities? capabilities)
    {
        Id = profile.Id;
        DisplayName = profile.DisplayName;
        Protocol = profile.Protocol;
        BaseUri = profile.BaseUri.AbsoluteUri;
        RequestPathOverride = profile.RequestPathOverride ?? string.Empty;
        Model = profile.Model;
        ContextWindowTokens = profile.ContextWindowTokens;
        AutomaticContextCompression = profile.AutomaticContextCompression;
        ContextCompressionThresholdPercent = profile.ContextCompressionThresholdPercent;
        CheckCertificateRevocation = profile.CheckCertificateRevocation;
        AuthenticationKind = profile.Authentication.Kind;
        CredentialReference = profile.Authentication.CredentialReference;
        HeaderName = profile.Authentication.HeaderName;
        HeaderPrefix = profile.Authentication.HeaderPrefix;
        ProxyMode = profile.Proxy.Mode;
        ProxyAddress = profile.Proxy.Address?.AbsoluteUri ?? string.Empty;
        ProxyUsernameCredentialReference = profile.Proxy.UsernameCredentialReference;
        ProxyPasswordCredentialReference = profile.Proxy.PasswordCredentialReference;
        BypassList = string.Join(Environment.NewLine, profile.Proxy.BypassList);
        HeadersJson = JsonSerializer.Serialize(
            profile.Headers,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        Capabilities = capabilities;
        TestStatus = DescribeCapabilities(capabilities);
    }

    public static AiConnectionEditorViewModel CreateNew(int index) => new(new AiConnectionProfile
    {
        Id = $"connection-{index}",
        DisplayName = $"连接 {index}",
        Protocol = AiProtocol.OpenAiResponses,
        BaseUri = new Uri("https://api.openai.com/v1/"),
        Model = string.Empty,
        Authentication = new AiAuthenticationConfiguration
        {
            Kind = AiAuthenticationKind.Bearer,
            CredentialReference = $"diary.ai/connection-{index}/api-key",
        },
        Proxy = new AiProxyConfiguration { Mode = AiProxyMode.System },
    }, null);

    public IReadOnlyList<AiProtocol> ProtocolOptions { get; } = Enum.GetValues<AiProtocol>();

    public IReadOnlyList<AiAuthenticationKind> AuthenticationOptions { get; } = Enum.GetValues<AiAuthenticationKind>();

    public IReadOnlyList<AiProxyMode> ProxyOptions { get; } = Enum.GetValues<AiProxyMode>();

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private AiProtocol _protocol;
    [ObservableProperty] private string _baseUri = string.Empty;
    [ObservableProperty] private string _requestPathOverride = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private int _contextWindowTokens = AiConnectionProfile.DefaultContextWindowTokens;
    [ObservableProperty] private bool _automaticContextCompression = true;
    [ObservableProperty] private int _contextCompressionThresholdPercent = 75;
    [ObservableProperty] private bool _checkCertificateRevocation = true;
    [ObservableProperty] private AiAuthenticationKind _authenticationKind;
    [ObservableProperty] private string _credentialReference = string.Empty;
    [ObservableProperty] private string _headerName = "Authorization";
    [ObservableProperty] private string _headerPrefix = "Bearer ";
    [ObservableProperty] private string _newCredential = string.Empty;
    [ObservableProperty] private string _credentialStatus = "未保存";
    [ObservableProperty] private AiProxyMode _proxyMode;
    [ObservableProperty] private string _proxyAddress = string.Empty;
    [ObservableProperty] private string _proxyUsernameCredentialReference = string.Empty;
    [ObservableProperty] private string _proxyPasswordCredentialReference = string.Empty;
    [ObservableProperty] private string _bypassList = string.Empty;
    [ObservableProperty] private string _headersJson = "[]";
    [ObservableProperty] private string _proxyUsername = string.Empty;
    [ObservableProperty] private string _proxyPassword = string.Empty;
    [ObservableProperty] private string _proxyUsernameStatus = "未保存";
    [ObservableProperty] private string _proxyPasswordStatus = "未保存";
    [ObservableProperty] private string _testStatus = "尚未测试";
    [ObservableProperty] private bool _isTesting;

    public AiConnectionCapabilities? Capabilities { get; set; }

    public AiConnectionProfile BuildProfile(bool temporaryCredential = false)
    {
        var normalizedId = Id.Trim();
        var credentialReference = temporaryCredential && !string.IsNullOrEmpty(NewCredential)
            ? $"memory:connection-test/{Guid.NewGuid():N}/api-key"
            : string.IsNullOrWhiteSpace(CredentialReference)
                ? $"diary.ai/{normalizedId}/api-key"
                : CredentialReference.Trim();
        var proxyUsernameReference = temporaryCredential && !string.IsNullOrEmpty(ProxyUsername)
            ? $"memory:connection-test/{Guid.NewGuid():N}/proxy-user"
            : string.IsNullOrWhiteSpace(ProxyUsernameCredentialReference)
                ? $"diary.ai/{normalizedId}/proxy-user"
                : ProxyUsernameCredentialReference.Trim();
        var proxyPasswordReference = temporaryCredential && !string.IsNullOrEmpty(ProxyPassword)
            ? $"memory:connection-test/{Guid.NewGuid():N}/proxy-password"
            : string.IsNullOrWhiteSpace(ProxyPasswordCredentialReference)
                ? $"diary.ai/{normalizedId}/proxy-password"
                : ProxyPasswordCredentialReference.Trim();
        var headers = JsonSerializer.Deserialize<AiRequestHeader[]>(
            string.IsNullOrWhiteSpace(HeadersJson) ? "[]" : HeadersJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
            }) ?? [];
        return new AiConnectionProfile
        {
            Id = normalizedId,
            DisplayName = DisplayName.Trim(),
            Protocol = Protocol,
            BaseUri = new Uri(BaseUri.Trim(), UriKind.Absolute),
            RequestPathOverride = string.IsNullOrWhiteSpace(RequestPathOverride)
                ? null
                : RequestPathOverride.Trim(),
            Model = Model.Trim(),
            ContextWindowTokens = ContextWindowTokens,
            AutomaticContextCompression = AutomaticContextCompression,
            ContextCompressionThresholdPercent = ContextCompressionThresholdPercent,
            CheckCertificateRevocation = CheckCertificateRevocation,
            Authentication = new AiAuthenticationConfiguration
            {
                Kind = AuthenticationKind,
                CredentialReference = AuthenticationKind == AiAuthenticationKind.None
                    ? string.Empty
                    : credentialReference,
                HeaderName = HeaderName.Trim(),
                HeaderPrefix = HeaderPrefix,
            },
            Proxy = new AiProxyConfiguration
            {
                Mode = ProxyMode,
                Address = string.IsNullOrWhiteSpace(ProxyAddress)
                    ? null
                    : new Uri(ProxyAddress.Trim(), UriKind.Absolute),
                BypassList = BypassList.Split(
                    ['\r', '\n'],
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                UsernameCredentialReference = ProxyMode == AiProxyMode.Custom
                    ? proxyUsernameReference
                    : string.Empty,
                PasswordCredentialReference = ProxyMode == AiProxyMode.Custom
                    ? proxyPasswordReference
                    : string.Empty,
            },
            Headers = headers,
        };
    }

    public IReadOnlySet<string> GetCredentialReferences()
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var normalizedId = Id.Trim();
        if (AuthenticationKind != AiAuthenticationKind.None)
        {
            AddReference(
                references,
                string.IsNullOrWhiteSpace(CredentialReference)
                    ? $"diary.ai/{normalizedId}/api-key"
                    : CredentialReference);
        }
        if (ProxyMode == AiProxyMode.Custom)
        {
            AddReference(
                references,
                string.IsNullOrWhiteSpace(ProxyUsernameCredentialReference)
                    ? $"diary.ai/{normalizedId}/proxy-user"
                    : ProxyUsernameCredentialReference);
            AddReference(
                references,
                string.IsNullOrWhiteSpace(ProxyPasswordCredentialReference)
                    ? $"diary.ai/{normalizedId}/proxy-password"
                    : ProxyPasswordCredentialReference);
        }
        try
        {
            var headers = JsonSerializer.Deserialize<AiRequestHeader[]>(
                string.IsNullOrWhiteSpace(HeadersJson) ? "[]" : HeadersJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    PropertyNameCaseInsensitive = true,
                }) ?? [];
            foreach (var header in headers)
                AddReference(references, header.CredentialReference);
        }
        catch (JsonException)
        {
            // 删除连接不应被尚未保存的 Header JSON 编辑错误阻断。
        }
        return references;
    }

    private static void AddReference(ISet<string> references, string? reference)
    {
        if (!string.IsNullOrWhiteSpace(reference))
            references.Add(reference.Trim());
    }

    private static string DescribeCapabilities(AiConnectionCapabilities? capabilities)
    {
        if (capabilities is null)
            return "尚未测试";
        if (!capabilities.SupportsChat)
            return $"测试失败（{capabilities.ErrorCode ?? "unknown"}）：{capabilities.ErrorMessage}";
        if (capabilities.ErrorCode == AiConnectionProbeService.BasicProbeOnlyCode)
            return capabilities.ErrorMessage ?? "基础连接成功；尚未执行完整能力探测。";
        if (capabilities.ErrorCode == AiConnectionProbeService.PartialProbeTimeoutCode)
        {
            var verified = new List<string> { "普通对话" };
            AddVerifiedCapability("流式文本", capabilities.SupportsStreaming, verified);
            AddVerifiedCapability("工具闭环", capabilities.SupportsTools, verified);
            AddVerifiedCapability("流式工具", capabilities.SupportsStreamingTools, verified);
            AddVerifiedCapability("并行工具", capabilities.SupportsParallelTools, verified);
            AddVerifiedCapability("强制工具", capabilities.SupportsForcedToolChoice, verified);
            return string.Join("、", verified) + "已通过；" + capabilities.ErrorMessage;
        }
        var supported = new List<string> { "普通对话" };
        var unavailable = new List<string>();
        AddCapability("流式文本", capabilities.SupportsStreaming, supported, unavailable);
        AddCapability("工具闭环", capabilities.SupportsTools, supported, unavailable);
        if (capabilities.SupportsTools)
        {
            AddCapability("流式工具", capabilities.SupportsStreamingTools, supported, unavailable);
            AddCapability("并行工具", capabilities.SupportsParallelTools, supported, unavailable);
            AddCapability("强制工具", capabilities.SupportsForcedToolChoice, supported, unavailable);
        }
        return unavailable.Count == 0
            ? string.Join("、", supported) + "均通过"
            : string.Join("、", supported) + "通过；" + string.Join("、", unavailable) + "不可用";
    }

    private static void AddCapability(
        string name,
        bool available,
        ICollection<string> supported,
        ICollection<string> unavailable)
    {
        (available ? supported : unavailable).Add(name);
    }

    private static void AddVerifiedCapability(string name, bool available, ICollection<string> verified)
    {
        if (available)
            verified.Add(name);
    }

    public void ApplyCapabilities(AiConnectionCapabilities capabilities)
    {
        Capabilities = capabilities;
        TestStatus = DescribeCapabilities(capabilities);
    }

    public void ApplyCredentialStatuses(string apiKey, string proxyUsername, string proxyPassword)
    {
        CredentialStatus = apiKey;
        ProxyUsernameStatus = proxyUsername;
        ProxyPasswordStatus = proxyPassword;
    }
}

public sealed partial class AiAgentSettingsViewModel : ViewModelBase
{
    private readonly AiConnectionManager _manager;
    private readonly IAiCredentialStore _credentials;
    private readonly AiAgentPageViewModel _page;
    private readonly HashSet<string> _pendingCredentialDeletes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _profileTestCancellation;
    private AiConnectionEditorViewModel? _testingProfile;

    public AiAgentSettingsViewModel(
        AiConnectionManager manager,
        IAiCredentialStore credentials,
        AiAgentPageViewModel page)
    {
        _manager = manager;
        _credentials = credentials;
        _page = page;
        Load();
        _ = RefreshCredentialStatusesAsync();
    }

    public ObservableCollection<AiConnectionEditorViewModel> Profiles { get; } = [];

    [ObservableProperty] private AiConnectionEditorViewModel? _selectedProfile;
    public bool HasSelectedProfile => SelectedProfile is not null;
    public bool HasNoSelectedProfile => SelectedProfile is null;

    partial void OnSelectedProfileChanged(AiConnectionEditorViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(HasNoSelectedProfile));
    }

    [ObservableProperty] private string? _defaultProfileId;
    [ObservableProperty] private bool _diaryToolsEnabled = true;
    [ObservableProperty] private bool _workItemWriteEnabled;
    [ObservableProperty] private bool _webSearchEnabled;
    [ObservableProperty] private bool _webFetchEnabled;
    [ObservableProperty] private bool _mcpEnabled;
    [ObservableProperty] private string _mcpServersJson = "[]";
    [ObservableProperty] private string _webAccessJson = "{}";
    [ObservableProperty] private string _defaultProxyJson = "{}";
    [ObservableProperty] private bool _deleteCredentialsWithProfile = true;
    [ObservableProperty] private string _saveStatus = string.Empty;

    [RelayCommand]
    private void AddProfile()
    {
        var index = 1;
        while (Profiles.Any(profile => profile.Id == $"connection-{index}"))
            index++;
        var profile = AiConnectionEditorViewModel.CreateNew(index);
        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    [RelayCommand]
    private void RemoveProfile()
    {
        var removed = SelectedProfile;
        if (removed is null)
            return;
        var removedId = removed.Id;
        var removedReferences = removed.GetCredentialReferences();
        Profiles.Remove(removed);
        SelectedProfile = Profiles.FirstOrDefault();
        if (DefaultProfileId == removedId)
            DefaultProfileId = SelectedProfile?.Id;
        if (!DeleteCredentialsWithProfile)
        {
            SaveStatus = "已移除连接；保存设置后生效，关联凭据已保留。";
            return;
        }
        _pendingCredentialDeletes.UnionWith(removedReferences);
        SaveStatus = "已移除连接；保存设置成功后将清理未被其他连接使用的本地凭据。";
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is null)
            return;
        try
        {
            var source = SelectedProfile.BuildProfile();
            var index = 1;
            var id = source.Id + "-copy";
            while (Profiles.Any(profile => profile.Id == id))
                id = source.Id + "-copy-" + ++index;
            var copy = source with
            {
                Id = id,
                DisplayName = source.DisplayName + " 副本",
                Authentication = source.Authentication with
                {
                    CredentialReference = source.Authentication.Kind == AiAuthenticationKind.None
                        ? string.Empty
                        : $"diary.ai/{id}/api-key",
                },
                Proxy = source.Proxy with
                {
                    UsernameCredentialReference = source.Proxy.Mode == AiProxyMode.Custom
                        ? $"diary.ai/{id}/proxy-user"
                        : string.Empty,
                    PasswordCredentialReference = source.Proxy.Mode == AiProxyMode.Custom
                        ? $"diary.ai/{id}/proxy-password"
                        : string.Empty,
                },
            };
            var editor = new AiConnectionEditorViewModel(copy, null);
            editor.ApplyCredentialStatuses("未保存", "未保存", "未保存");
            Profiles.Add(editor);
            SelectedProfile = editor;
            SaveStatus = "已复制连接配置；凭据不会复制，请填写后测试并保存。";
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException or JsonException)
        {
            SaveStatus = $"复制失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private void SetDefaultProfile()
    {
        if (SelectedProfile is null)
            return;
        DefaultProfileId = SelectedProfile.Id.Trim();
        SaveStatus = $"已选择默认连接：{SelectedProfile.DisplayName}。保存设置后生效。";
    }

    [RelayCommand]
    private Task TestProfile() => RunProfileTestAsync(fullCapabilities: false);

    [RelayCommand]
    private Task ProbeProfileCapabilities() => RunProfileTestAsync(fullCapabilities: true);

    [RelayCommand]
    private void CancelProfileTest()
    {
        if (_profileTestCancellation is null)
            return;
        if (_testingProfile is not null)
            _testingProfile.TestStatus = "正在取消连接测试…";
        _profileTestCancellation.Cancel();
    }

    private async Task RunProfileTestAsync(bool fullCapabilities)
    {
        var editor = SelectedProfile;
        if (editor is null || _profileTestCancellation is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        _profileTestCancellation = cancellation;
        _testingProfile = editor;
        editor.IsTesting = true;
        editor.TestStatus = fullCapabilities
            ? "正在准备完整能力探测…"
            : "正在准备基础连接测试…";
        AiConnectionProfile? profile = null;
        try
        {
            var useTemporaryCredentials = !string.IsNullOrEmpty(editor.NewCredential)
                || !string.IsNullOrEmpty(editor.ProxyUsername)
                || !string.IsNullOrEmpty(editor.ProxyPassword);
            profile = editor.BuildProfile(useTemporaryCredentials);
            if (profile.Authentication.Kind != AiAuthenticationKind.None
                && !string.IsNullOrEmpty(editor.NewCredential))
            {
                await _credentials.SetAsync(
                    profile.Authentication.CredentialReference,
                    editor.NewCredential.AsMemory());
            }
            if (profile.Proxy.Mode == AiProxyMode.Custom)
            {
                if (!string.IsNullOrEmpty(editor.ProxyUsername))
                    await _credentials.SetAsync(profile.Proxy.UsernameCredentialReference, editor.ProxyUsername.AsMemory());
                if (!string.IsNullOrEmpty(editor.ProxyPassword))
                    await _credentials.SetAsync(profile.Proxy.PasswordCredentialReference, editor.ProxyPassword.AsMemory());
            }
            var progress = new Progress<AiConnectionProbeProgress>(item =>
            {
                editor.TestStatus = $"正在测试：{item.DisplayName}（{item.Current}/{item.Total}）…";
            });
            var result = fullCapabilities
                ? await _manager.TestAsync(profile, progress, cancellation.Token)
                : await _manager.TestConnectionAsync(profile, progress, cancellation.Token);
            editor.ApplyCapabilities(result);
        }
        catch (OperationCanceledException)
        {
            editor.TestStatus = "连接测试已取消。";
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException or InvalidOperationException)
        {
            editor.TestStatus = $"配置无效：{exception.Message}";
        }
        finally
        {
            if (profile is not null)
            {
                foreach (var reference in new[]
                         {
                             profile.Authentication.CredentialReference,
                             profile.Proxy.UsernameCredentialReference,
                             profile.Proxy.PasswordCredentialReference,
                         }.Where(reference => reference.StartsWith("memory:", StringComparison.Ordinal)))
                {
                    await _credentials.DeleteAsync(reference);
                }
            }
            editor.IsTesting = false;
            if (ReferenceEquals(_profileTestCancellation, cancellation))
                _profileTestCancellation = null;
            if (ReferenceEquals(_testingProfile, editor))
                _testingProfile = null;
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        try
        {
            var profiles = Profiles.Select(profile => profile.BuildProfile()).ToArray();
            foreach (var editor in Profiles)
            {
                var profile = profiles.Single(item => item.Id == editor.Id.Trim());
                if (profile.Authentication.Kind != AiAuthenticationKind.None
                    && !string.IsNullOrEmpty(editor.NewCredential))
                {
                    await _credentials.SetAsync(
                        profile.Authentication.CredentialReference,
                        editor.NewCredential.AsMemory());
                }
                if (profile.Proxy.Mode == AiProxyMode.Custom)
                {
                    if (!string.IsNullOrEmpty(editor.ProxyUsername))
                    {
                        await _credentials.SetAsync(
                            profile.Proxy.UsernameCredentialReference,
                            editor.ProxyUsername.AsMemory());
                    }
                    if (!string.IsNullOrEmpty(editor.ProxyPassword))
                    {
                        await _credentials.SetAsync(
                            profile.Proxy.PasswordCredentialReference,
                            editor.ProxyPassword.AsMemory());
                    }
                }
            }
            var capabilities = Profiles
                .Where(profile => profile.Capabilities is not null)
                .ToDictionary(profile => profile.Id.Trim(), profile => profile.Capabilities!, StringComparer.Ordinal);
            var mcpServers = JsonSerializer.Deserialize<McpServerProfile[]>(
                string.IsNullOrWhiteSpace(McpServersJson) ? "[]" : McpServersJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    PropertyNameCaseInsensitive = true,
                }) ?? [];
            var webAccess = JsonSerializer.Deserialize<WebAccessPolicy>(
                string.IsNullOrWhiteSpace(WebAccessJson) ? "{}" : WebAccessJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    PropertyNameCaseInsensitive = true,
                }) ?? new WebAccessPolicy();
            var defaultProxy = JsonSerializer.Deserialize<AiProxyConfiguration>(
                string.IsNullOrWhiteSpace(DefaultProxyJson) ? "{}" : DefaultProxyJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    PropertyNameCaseInsensitive = true,
                }) ?? new AiProxyConfiguration { Mode = AiProxyMode.System };
            var settings = new AiAgentSettings
            {
                DefaultProfileId = string.IsNullOrWhiteSpace(DefaultProfileId) ? profiles.FirstOrDefault()?.Id : DefaultProfileId,
                Profiles = profiles,
                Capabilities = capabilities,
                McpServers = mcpServers,
                WebAccess = webAccess,
                DefaultProxy = defaultProxy,
                EnabledTools = new AiAgentToolSettings
                {
                    Diary = DiaryToolsEnabled,
                    WorkItemWrite = WorkItemWriteEnabled,
                    WebSearch = WebSearchEnabled,
                    WebFetch = WebFetchEnabled,
                    Mcp = McpEnabled,
                },
            };
            _manager.SaveWorkingCopy(settings);
            var cleanupSummary = string.Empty;
            if (_pendingCredentialDeletes.Count > 0)
            {
                try
                {
                    var retainedReferences = Profiles
                        .SelectMany(profile => profile.GetCredentialReferences())
                        .ToHashSet(StringComparer.Ordinal);
                    var deleted = await AiCredentialReferenceCleanup.DeleteUnusedLocalReferencesAsync(
                        _credentials,
                        _pendingCredentialDeletes,
                        retainedReferences);
                    _pendingCredentialDeletes.Clear();
                    cleanupSummary = deleted == 0
                        ? "没有需要清理的独占本地凭据。"
                        : $"已清理 {deleted} 个不再使用的本地凭据。";
                }
                catch (Exception exception) when (exception is ArgumentException
                                                   or InvalidOperationException
                                                   or IOException)
                {
                    cleanupSummary = $"连接设置已保存，但本地凭据清理失败，可再次保存重试：{exception.Message}";
                }
            }
            foreach (var editor in Profiles)
            {
                editor.NewCredential = string.Empty;
                editor.ProxyUsername = string.Empty;
                editor.ProxyPassword = string.Empty;
            }
            await RefreshCredentialStatusesAsync();
            _page.RefreshProfiles();
            SaveStatus = "设置已保存。凭据正文保存在加密文件、环境变量或当前会话内，不写入 settings.json。"
                         + (string.IsNullOrEmpty(cleanupSummary) ? string.Empty : " " + cleanupSummary);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or UriFormatException
                                           or InvalidOperationException
                                           or JsonException
                                           or IOException)
        {
            SaveStatus = $"保存失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private void Reload() => Load();

    private void Load()
    {
        _pendingCredentialDeletes.Clear();
        Profiles.Clear();
        foreach (var profile in _manager.Settings.Profiles)
        {
            _manager.Capabilities.TryGetValue(profile.Id, out var capabilities);
            Profiles.Add(new AiConnectionEditorViewModel(profile, capabilities));
        }
        DefaultProfileId = _manager.Settings.DefaultProfileId;
        DiaryToolsEnabled = _manager.Settings.EnabledTools.Diary;
        WorkItemWriteEnabled = _manager.Settings.EnabledTools.WorkItemWrite;
        WebSearchEnabled = _manager.Settings.EnabledTools.WebSearch;
        WebFetchEnabled = _manager.Settings.EnabledTools.WebFetch;
        McpEnabled = _manager.Settings.EnabledTools.Mcp;
        McpServersJson = JsonSerializer.Serialize(
            _manager.Settings.McpServers,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        WebAccessJson = JsonSerializer.Serialize(
            _manager.Settings.WebAccess,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        DefaultProxyJson = JsonSerializer.Serialize(
            _manager.Settings.DefaultProxy,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == DefaultProfileId) ?? Profiles.FirstOrDefault();
        SaveStatus = _manager.LoadStatus == AiSettingsLoadStatus.Unreadable
            ? $"设置文件不可读取，已阻止覆盖：{_manager.LoadError}"
            : string.Empty;
        _ = RefreshCredentialStatusesAsync();
    }

    private async Task RefreshCredentialStatusesAsync()
    {
        foreach (var editor in Profiles)
        {
            var profile = editor.BuildProfile();
            editor.ApplyCredentialStatuses(
                await DescribeCredentialAsync(profile.Authentication.Kind == AiAuthenticationKind.None
                    ? string.Empty
                    : profile.Authentication.CredentialReference),
                await DescribeCredentialAsync(profile.Proxy.Mode == AiProxyMode.Custom
                    ? profile.Proxy.UsernameCredentialReference
                    : string.Empty),
                await DescribeCredentialAsync(profile.Proxy.Mode == AiProxyMode.Custom
                    ? profile.Proxy.PasswordCredentialReference
                    : string.Empty));
        }
    }

    private async ValueTask<string> DescribeCredentialAsync(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "无需凭据";
        var exists = await _credentials.ExistsAsync(reference);
        if (reference.StartsWith("env:", StringComparison.Ordinal))
            return exists ? "来自环境变量" : "环境变量未设置";
        if (reference.StartsWith("memory:", StringComparison.Ordinal))
            return exists ? "当前会话已设置" : "当前会话未设置";
        return exists ? "已保存" : "未保存";
    }
}
