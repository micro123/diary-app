using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Agent.Configuration;
using Diary.Agent.Runtime;
using Diary.Agent.Tools;
using Diary.Agent.Mcp;
using Diary.Agent.Protocols;
using Diary.GUIBase.ViewModels;
using Diary.ScriptHost;

namespace Diary.Agent.UI.ViewModels;

public sealed partial class AiChatMessageViewModel(string role, string content) : ObservableObject
{
    public string Role { get; } = role;

    [ObservableProperty]
    private string _content = content;

    [ObservableProperty]
    private string _reasoning = string.Empty;

    [ObservableProperty]
    private bool _isThinking;

    public bool HasReasoning => IsThinking || !string.IsNullOrWhiteSpace(Reasoning);

    public string ReasoningDisplayText => string.IsNullOrWhiteSpace(Reasoning)
        ? "模型正在思考…"
        : Reasoning;

    partial void OnReasoningChanged(string value)
    {
        OnPropertyChanged(nameof(HasReasoning));
        OnPropertyChanged(nameof(ReasoningDisplayText));
    }

    partial void OnIsThinkingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasReasoning));
        OnPropertyChanged(nameof(ReasoningDisplayText));
    }
}

public sealed partial class AiToolCallViewModel(
    string name,
    string callId,
    string argumentsSummary) : ObservableObject
{
    public string Name { get; } = name;

    public string CallId { get; } = callId;

    public string ArgumentsSummary { get; } = argumentsSummary;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    [ObservableProperty]
    private string _status = "执行中";

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _durationText = string.Empty;
}

public sealed partial class AiWorkItemConfirmationViewModel : ObservableObject
{
    public AiWorkItemConfirmationViewModel(WorkItemConfirmationRequest request)
    {
        ConfirmationId = request.ConfirmationId;
        IdempotencyKey = request.Command.IdempotencyKey;
        PreviewVersion = request.PreviewVersion;
        Date = request.Command.Date;
        Title = request.Command.Title;
        Hours = request.Command.Hours;
        Priority = request.Command.Priority;
        TagIdsText = string.Join(", ", request.Command.TagIds);
        Note = request.Command.Note ?? string.Empty;
        ExtraFieldsJson = JsonSerializer.Serialize(
            request.Command.ExtraFields,
            new JsonSerializerOptions { WriteIndented = true });
    }

    public Guid ConfirmationId { get; }

    public string IdempotencyKey { get; }

    public string PreviewVersion { get; }

    [ObservableProperty] private string _date;
    [ObservableProperty] private string _title;
    [ObservableProperty] private double _hours;
    [ObservableProperty] private int _priority;
    [ObservableProperty] private string _tagIdsText;
    [ObservableProperty] private string _note;
    [ObservableProperty] private string _extraFieldsJson;
    [ObservableProperty] private string _validationMessage = string.Empty;

    public bool TryBuildEditedCommand(out WorkItemCreateCommand? command)
    {
        command = null;
        try
        {
            var tagIds = TagIdsText.Split(
                    [',', '，', ' ', '\r', '\n', '\t'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .ToArray();
            var extraFields = string.IsNullOrWhiteSpace(ExtraFieldsJson)
                ? []
                : JsonSerializer.Deserialize<WorkItemExtraFieldCommand[]>(ExtraFieldsJson)
                  ?? [];
            command = new WorkItemCreateCommand(
                Date,
                Title,
                Hours,
                Priority,
                tagIds,
                extraFields,
                string.IsNullOrWhiteSpace(Note) ? null : Note,
                IdempotencyKey);
            ValidationMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or JsonException)
        {
            ValidationMessage = "标签 ID 或附加字段 JSON 格式无效。";
            return false;
        }
    }
}

public sealed class AiExternalToolConfirmationViewModel(ExternalToolConfirmationRequest request)
{
    public Guid ConfirmationId { get; } = request.ConfirmationId;

    public string ServerName { get; } = request.ServerName;

    public string ToolName { get; } = request.ToolName;

    public string ArgumentsJson { get; } = JsonSerializer.Serialize(
        request.Arguments,
        new JsonSerializerOptions { WriteIndented = true });
}

public sealed class AiConversationItemViewModel
{
    public AiConversationItemViewModel(AgentConversationRecord record)
    {
        Record = record;
        DisplayText = $"{record.Title} · {record.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    public AgentConversationRecord Record { get; }

    public string DisplayText { get; }
}

public sealed partial class AiAgentPageViewModel : ViewModelBase
{
    private readonly AiConnectionManager _connections;
    private readonly AgentSessionService _session;
    private readonly AgentToolRegistry _tools;
    private readonly AgentConfirmationCoordinator _confirmations;
    private readonly McpClientManager _mcp;
    private readonly AgentConversationStore _conversationStore;
    private Guid _conversationId = Guid.NewGuid();
    private DateTimeOffset _conversationCreatedAtUtc = DateTimeOffset.UtcNow;
    private bool _suppressProfileSessionReset;

    public AiAgentPageViewModel(
        AiConnectionManager connections,
        AgentSessionService session,
        AgentToolRegistry tools,
        AgentConfirmationCoordinator confirmations,
        McpClientManager mcp,
        AgentConversationStore conversationStore)
    {
        _connections = connections;
        _session = session;
        _tools = tools;
        _confirmations = confirmations;
        _mcp = mcp;
        _conversationStore = conversationStore;
        _confirmations.ConfirmationRequested += OnConfirmationRequested;
        _confirmations.ExternalConfirmationRequested += OnExternalConfirmationRequested;
        _confirmations.ConfirmationCompleted += OnConfirmationCompleted;
        RefreshProfiles();
        _ = LoadConversationListAsync();
    }

    public override bool IsViewCacheable => true;

    public ObservableCollection<AiConnectionProfile> Profiles { get; } = [];

    public ObservableCollection<AiChatMessageViewModel> Messages { get; } = [];

    public ObservableCollection<AiToolCallViewModel> ToolCalls { get; } = [];

    public ObservableCollection<AiConversationItemViewModel> Conversations { get; } = [];

    [ObservableProperty]
    private AiConnectionProfile? _selectedProfile;

    [ObservableProperty]
    private AiConversationItemViewModel? _selectedConversation;

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string _statusText = "请在 AI 助手设置中添加并测试连接。";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingConfirmation))]
    private AiWorkItemConfirmationViewModel? _pendingConfirmation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingExternalConfirmation))]
    private AiExternalToolConfirmationViewModel? _pendingExternalConfirmation;

    public bool HasPendingConfirmation => PendingConfirmation is not null;

    public bool HasPendingExternalConfirmation => PendingExternalConfirmation is not null;

    public bool IsEmpty => Messages.Count == 0;

    public string ModeText
    {
        get
        {
            if (SelectedProfile is null)
                return "未配置";
            return TryGetCapabilities(SelectedProfile, out var capabilities) && capabilities.SupportsTools
                ? "Agent 模式"
                : "普通对话";
        }
    }

    partial void OnSelectedProfileChanged(AiConnectionProfile? oldValue, AiConnectionProfile? newValue)
    {
        OnPropertyChanged(nameof(ModeText));
        if (!_suppressProfileSessionReset
            && oldValue is not null
            && newValue is not null
            && oldValue.Id != newValue.Id
            && Messages.Count > 0
            && !IsBusy)
        {
            BeginNewSession();
            StatusText = "已切换连接，并创建新的模型上下文。";
            return;
        }
        StatusText = newValue is null
            ? "请在 AI 助手设置中添加并测试连接。"
            : DescribeConnection(newValue);
    }

    [RelayCommand]
    private async Task Send()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Input))
            return;
        if (SelectedProfile is null)
        {
            StatusText = "没有可用连接，请先打开 AI 助手设置。";
            return;
        }
        if (!TryGetCapabilities(SelectedProfile, out var capabilities) || !capabilities.SupportsChat)
        {
            StatusText = "连接尚未通过测试，暂不能发送请求。";
            return;
        }

        var userText = Input.Trim();
        Input = string.Empty;
        Messages.Add(new AiChatMessageViewModel("你", userText));
        var assistant = new AiChatMessageViewModel("AI", string.Empty);
        Messages.Add(assistant);
        OnPropertyChanged(nameof(IsEmpty));
        ToolCalls.Clear();
        IsBusy = true;
        StatusText = "正在生成…";
        var toolCards = new Dictionary<string, AiToolCallViewModel>(StringComparer.Ordinal);
        var progress = new Progress<AgentRunEvent>(item =>
        {
            switch (item.Kind)
            {
                case AgentRunEventKind.ModelRequestStarted:
                    assistant.IsThinking = false;
                    StatusText = item.Text ?? "正在等待模型响应…";
                    break;
                case AgentRunEventKind.ReasoningDelta:
                    assistant.IsThinking = true;
                    if (!string.IsNullOrEmpty(item.Text))
                        assistant.Reasoning += item.Text;
                    StatusText = "模型正在思考…";
                    break;
                case AgentRunEventKind.TextDelta:
                    assistant.IsThinking = false;
                    assistant.Content += item.Text;
                    StatusText = "模型正在生成回答…";
                    break;
                case AgentRunEventKind.ToolStarted:
                    {
                        assistant.IsThinking = false;
                        var startedCard = new AiToolCallViewModel(
                            item.ToolName ?? "未知工具",
                            item.ToolCallId ?? string.Empty,
                            item.ToolArgumentsSummary ?? "{}");
                        toolCards[startedCard.CallId] = startedCard;
                        ToolCalls.Add(startedCard);
                        break;
                    }
                case AgentRunEventKind.ToolCompleted:
                    if (item.ToolCallId is not null && toolCards.TryGetValue(item.ToolCallId, out var completedCard))
                    {
                        completedCard.Status = item.ToolResult?.Succeeded == true ? "已完成" : "失败";
                        completedCard.Summary = item.ToolResult?.Succeeded == true
                            ? CreateResultSummary(item.ToolResult.Content)
                            : item.ToolResult?.Content ?? "工具调用失败";
                        completedCard.Source = item.ToolResult?.Source ?? string.Empty;
                        completedCard.DurationText = $"{Math.Max(0, (DateTimeOffset.UtcNow - completedCard.StartedAtUtc).TotalMilliseconds):F0} ms";
                    }
                    break;
                case AgentRunEventKind.ContextCompacted:
                    StatusText = item.Text ?? "已自动压缩较早的会话历史。";
                    break;
                case AgentRunEventKind.Completed:
                case AgentRunEventKind.Failed:
                    assistant.IsThinking = false;
                    break;
            }
        });
        try
        {
            if (_connections.Settings.EnabledTools.Mcp)
                await _mcp.RefreshToolsAsync(_tools);
            var result = await _session.RunAsync(
                userText,
                SelectedProfile,
                CreateEnabledToolSnapshot(),
                new AgentRunOptions(
                    SupportsStreaming: capabilities.SupportsStreaming,
                    SupportsTools: capabilities.SupportsTools,
                    SupportsStreamingTools: capabilities.SupportsStreamingTools),
                progress);
            StatusText = result.Status switch
            {
                AgentSessionStatus.Completed => _session.ContextCompactionCount > 0
                    ? $"已完成 · {result.Rounds} 轮 · {result.ToolCalls} 次工具调用 · 已压缩 {_session.ContextCompactionCount} 次"
                    : $"已完成 · {result.Rounds} 轮 · {result.ToolCalls} 次工具调用",
                AgentSessionStatus.Cancelled => "已停止；未完成内容不会进入下一轮历史。",
                _ => $"运行失败：{result.ErrorMessage}",
            };
            if (result.Status == AgentSessionStatus.Completed)
                await SaveConversationAsync(result, SelectedProfile);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _session.Cancel();
        StatusText = "正在停止…";
    }

    [RelayCommand]
    private void ConfirmWrite()
    {
        if (PendingConfirmation is null)
            return;
        _confirmations.Complete(
            PendingConfirmation.ConfirmationId,
            new WorkItemConfirmationResponse(AgentConfirmationDecision.Confirm));
    }

    [RelayCommand]
    private void EditAndConfirmWrite()
    {
        if (PendingConfirmation is null
            || !PendingConfirmation.TryBuildEditedCommand(out var command)
            || command is null)
        {
            return;
        }
        _confirmations.Complete(
            PendingConfirmation.ConfirmationId,
            new WorkItemConfirmationResponse(AgentConfirmationDecision.EditAndConfirm, command));
    }

    [RelayCommand]
    private void RejectWrite()
    {
        if (PendingConfirmation is null)
            return;
        _confirmations.Complete(
            PendingConfirmation.ConfirmationId,
            new WorkItemConfirmationResponse(AgentConfirmationDecision.Reject));
    }

    [RelayCommand]
    private void ConfirmExternalTool()
    {
        if (PendingExternalConfirmation is null)
            return;
        _confirmations.CompleteExternal(
            PendingExternalConfirmation.ConfirmationId,
            AgentConfirmationDecision.Confirm);
    }

    [RelayCommand]
    private void RejectExternalTool()
    {
        if (PendingExternalConfirmation is null)
            return;
        _confirmations.CompleteExternal(
            PendingExternalConfirmation.ConfirmationId,
            AgentConfirmationDecision.Reject);
    }

    [RelayCommand]
    private void NewSession()
    {
        if (IsBusy)
            return;
        BeginNewSession();
        RefreshProfiles();
        StatusText = SelectedProfile is null ? "请先配置连接。" : DescribeConnection(SelectedProfile);
    }

    [RelayCommand]
    private void LoadConversation()
    {
        if (IsBusy || SelectedConversation is null)
            return;
        var record = SelectedConversation.Record;
        var profile = Profiles.FirstOrDefault(item => item.Id == record.ConnectionId);
        if (profile is null)
        {
            StatusText = "该会话使用的连接已不存在，无法恢复。";
            return;
        }
        _suppressProfileSessionReset = true;
        try
        {
            SelectedProfile = profile;
        }
        finally
        {
            _suppressProfileSessionReset = false;
        }
        _conversationId = record.Id;
        _conversationCreatedAtUtc = record.CreatedAtUtc;
        Messages.Clear();
        foreach (var message in record.Messages)
            Messages.Add(new AiChatMessageViewModel(message.Role == "user" ? "你" : "AI", message.Content));
        ToolCalls.Clear();
        foreach (var tool in record.ToolCalls)
        {
            var card = new AiToolCallViewModel(tool.Name, string.Empty, tool.ArgumentsSummary)
            {
                Status = tool.Status,
                Source = tool.Source ?? string.Empty,
                DurationText = tool.Duration ?? string.Empty,
            };
            ToolCalls.Add(card);
        }
        _session.RestoreSession(
            record.Messages.Select(message =>
                message.Role == "user"
                    ? AgentMessage.User(message.Content)
                    : AgentMessage.Assistant(message.Content)),
            record.ContextSummary,
            record.ContextCompactionCount);
        OnPropertyChanged(nameof(IsEmpty));
        StatusText = $"已恢复会话：{record.Title}";
    }

    [RelayCommand]
    private async Task DeleteConversation()
    {
        if (IsBusy || SelectedConversation is null)
            return;
        var id = SelectedConversation.Record.Id;
        await _conversationStore.DeleteAsync(id);
        if (_conversationId == id)
            BeginNewSession();
        await LoadConversationListAsync();
        StatusText = "会话已删除。";
    }

    public void RefreshProfiles()
    {
        var selectedId = SelectedProfile?.Id ?? _connections.Settings.DefaultProfileId;
        Profiles.Clear();
        foreach (var profile in _connections.Settings.Profiles)
            Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(ModeText));
    }

    private bool TryGetCapabilities(
        AiConnectionProfile profile,
        out AiConnectionCapabilities capabilities)
        => _connections.Capabilities.TryGetValue(profile.Id, out capabilities!);

    private AgentToolSnapshot CreateEnabledToolSnapshot()
    {
        var settings = _connections.Settings.EnabledTools;
        return _tools.CreateSnapshot(descriptor => descriptor.Origin switch
        {
            AgentToolOrigin.BuiltIn when descriptor.Risk == AgentToolRisk.Write => settings.WorkItemWrite,
            AgentToolOrigin.BuiltIn => settings.Diary,
            AgentToolOrigin.ExternalWeb when descriptor.ModelName == "web_search" => settings.WebSearch,
            AgentToolOrigin.ExternalWeb => settings.WebFetch,
            AgentToolOrigin.Mcp => settings.Mcp,
            _ => true,
        });
    }

    private string DescribeConnection(AiConnectionProfile profile) =>
        TryGetCapabilities(profile, out var capabilities)
            ? capabilities.SupportsChat
                ? capabilities.SupportsTools
                    ? capabilities.ErrorCode == AiConnectionProbeService.PartialProbeTimeoutCode
                        ? "连接已通过 Agent 工具闭环测试；部分扩展能力探测超时，将自动降级。"
                        : "连接已通过 Agent 工具闭环测试。"
                    : capabilities.ErrorCode == AiConnectionProbeService.PartialProbeTimeoutCode
                        ? "普通对话已通过；工具闭环尚未确认，不会发送 DiaryApp 工具。"
                        : "连接仅支持普通对话，不会发送 DiaryApp 工具。"
                : $"连接测试失败：{capabilities.ErrorMessage}"
            : "连接尚未测试。";

    private static string CreateResultSummary(string content)
    {
        const int limit = 160;
        return content.Length <= limit ? content : content[..limit] + "…";
    }

    private void BeginNewSession()
    {
        _session.NewSession();
        _conversationId = Guid.NewGuid();
        _conversationCreatedAtUtc = DateTimeOffset.UtcNow;
        SelectedConversation = null;
        Messages.Clear();
        ToolCalls.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task SaveConversationAsync(AgentRunResult result, AiConnectionProfile profile)
    {
        var messages = _session.Messages
            .Where(message => message.Role == AgentMessageRole.User
                              || (message.Role == AgentMessageRole.Assistant
                                  && message.ToolCalls.Count == 0
                                  && !string.IsNullOrWhiteSpace(message.Text)))
            .Select(message => new AgentConversationMessage(
                message.Role == AgentMessageRole.User ? "user" : "assistant",
                message.Text))
            .ToArray();
        var firstUser = Messages.FirstOrDefault(message => message.Role == "你")?.Content
            ?? messages.FirstOrDefault(message => message.Role == "user")?.Content
            ?? "新会话";
        var title = firstUser.Length <= 60 ? firstUser : firstUser[..60] + "…";
        var tools = ToolCalls.Select(tool => new AgentConversationToolCall(
            tool.Name,
            tool.ArgumentsSummary,
            tool.Status,
            string.IsNullOrWhiteSpace(tool.Source) ? null : tool.Source,
            string.IsNullOrWhiteSpace(tool.DurationText) ? null : tool.DurationText)).ToArray();
        await _conversationStore.SaveAsync(new AgentConversationRecord(
            _conversationId,
            title,
            profile.Id,
            profile.Model,
            _conversationCreatedAtUtc,
            DateTimeOffset.UtcNow,
            messages,
            tools,
            result.Status.ToString(),
            result.Usage,
            _session.ContextSummary,
            _session.ContextCompactionCount));
        await LoadConversationListAsync();
        SelectedConversation = Conversations.FirstOrDefault(item => item.Record.Id == _conversationId);
    }

    private async Task LoadConversationListAsync()
    {
        var records = await _conversationStore.LoadAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selectedId = SelectedConversation?.Record.Id;
            Conversations.Clear();
            foreach (var record in records)
                Conversations.Add(new AiConversationItemViewModel(record));
            SelectedConversation = Conversations.FirstOrDefault(item => item.Record.Id == selectedId);
        });
    }

    private void OnConfirmationRequested(object? sender, WorkItemConfirmationRequest request) =>
        Dispatcher.UIThread.Post(() =>
        {
            PendingConfirmation = new AiWorkItemConfirmationViewModel(request);
            StatusText = "事项写入正在等待你的确认。";
        });

    private void OnExternalConfirmationRequested(object? sender, ExternalToolConfirmationRequest request) =>
        Dispatcher.UIThread.Post(() =>
        {
            PendingExternalConfirmation = new AiExternalToolConfirmationViewModel(request);
            StatusText = "写工具正在等待你的确认。";
        });

    private void OnConfirmationCompleted(Guid confirmationId) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (PendingConfirmation?.ConfirmationId == confirmationId)
                PendingConfirmation = null;
            if (PendingExternalConfirmation?.ConfirmationId == confirmationId)
                PendingExternalConfirmation = null;
        });
}
