using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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

public sealed partial class AiChatMessageViewModel(
    string role,
    string content,
    IReadOnlyList<AiImageAttachmentViewModel>? images = null) : ObservableObject
{
    public string Role { get; } = role;

    public IReadOnlyList<AiImageAttachmentViewModel> Images { get; } = images ?? [];

    public bool HasImages => Images.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserActions))]
    private string _content = content;

    [ObservableProperty]
    private string _reasoning = string.Empty;

    [ObservableProperty]
    private bool _isThinking;

    public bool HasReasoning => IsThinking || !string.IsNullOrWhiteSpace(Reasoning);

    public bool IsUser => string.Equals(Role, "你", StringComparison.Ordinal);

    public bool HasUserActions => IsUser && !string.IsNullOrWhiteSpace(Content);

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string _output = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputButtonText))]
    private bool _isOutputVisible;

    public bool HasOutput => !string.IsNullOrEmpty(Output);

    public string OutputButtonText => IsOutputVisible ? "收起输出" : "查看输出";

    [RelayCommand]
    private void ToggleOutput()
    {
        if (HasOutput)
            IsOutputVisible = !IsOutputVisible;
    }
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
        ExtraFieldsJson = AgentJson.Serialize(request.Command.ExtraFields, writeIndented: true);
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

    public string ArgumentsJson { get; } = AgentJson.Format(request.Arguments);

    public string PreviewTitle { get; } = request.PreviewTitle ?? "确认写工具调用";

    public string PreviewText { get; } = request.PreviewText ?? string.Empty;

    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewText);
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
    private static readonly IBrush ContextNormalBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush ContextWarningBrush = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush ContextCriticalBrush = new SolidColorBrush(Color.Parse("#DC2626"));
    private readonly AiConnectionManager _connections;
    private readonly AgentSessionService _session;
    private readonly AgentToolRegistry _tools;
    private readonly AgentConfirmationCoordinator _confirmations;
    private readonly McpClientManager _mcp;
    private readonly AgentConversationStore _conversationStore;
    private readonly PromptHistoryNavigator _promptHistory = new();
    private Guid _conversationId = Guid.NewGuid();
    private DateTimeOffset _conversationCreatedAtUtc = DateTimeOffset.UtcNow;
    private bool _suppressProfileSessionReset;
    private TaskCompletionSource? _activeSendDetached;
    private long _sendGeneration;
    private bool _applyingPromptHistory;

    public event EventHandler? InputFocusRequested;

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
        _ = InitializeConversationsAsync();
    }

    public override bool IsViewCacheable => true;

    public ObservableCollection<AiConnectionProfile> Profiles { get; } = [];

    public ObservableCollection<AiChatMessageViewModel> Messages { get; } = [];

    public ObservableCollection<AiToolCallViewModel> ToolCalls { get; } = [];

    public ObservableCollection<AiImageAttachmentViewModel> PendingImages { get; } = [];

    public ObservableCollection<AiConversationItemViewModel> Conversations { get; } = [];

    [ObservableProperty]
    private AiConnectionProfile? _selectedProfile;

    [ObservableProperty]
    private AiConversationItemViewModel? _selectedConversation;

    [ObservableProperty]
    private string _input = string.Empty;

    partial void OnInputChanged(string value)
    {
        if (!_applyingPromptHistory)
            _promptHistory.UpdateDraft(value);
    }

    [ObservableProperty]
    private string _statusText = "请在 AI 助手设置中添加并测试连接。";

    [ObservableProperty]
    private string _contextUsageText = "上下文 —";

    [ObservableProperty]
    private string _contextUsageToolTip = "选择模型连接后显示上下文窗口。";

    [ObservableProperty]
    private IBrush _contextUsageBrush = ContextNormalBrush;

    [ObservableProperty]
    private bool _isContextUsageVisible;

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

    public bool HasPendingImages => PendingImages.Count > 0;

    public bool CanAttachImages => SelectedProfile switch
    {
        { ImageInputMode: AiImageInputMode.Enabled } => true,
        { ImageInputMode: AiImageInputMode.Disabled } => false,
        { } profile => TryGetCapabilities(profile, out var capabilities) && capabilities.SupportsImageInput,
        _ => false,
    };

    public string ImageInputHint => SelectedProfile switch
    {
        null => "请先选择模型连接。",
        { ImageInputMode: AiImageInputMode.Disabled } => "当前连接已关闭图片输入。",
        { ImageInputMode: AiImageInputMode.Enabled } => "当前连接已强制启用图片输入。",
        { } profile when TryGetCapabilities(profile, out var capabilities) && capabilities.SupportsImageInput =>
            "完整能力探测已确认图片输入。",
        _ => "图片输入使用 Auto，但完整能力探测尚未确认支持。",
    };

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
        OnPropertyChanged(nameof(CanAttachImages));
        OnPropertyChanged(nameof(ImageInputHint));
        ResetContextUsage(newValue);
        if (newValue is not null && !CanAttachImages && PendingImages.Count > 0)
            ClearPendingImages();
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
        if (IsBusy || (string.IsNullOrWhiteSpace(Input) && PendingImages.Count == 0))
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
        if (PendingImages.Count > 0 && !CanAttachImages)
        {
            StatusText = "当前连接尚未启用或确认图片输入，请调整连接设置或执行完整能力探测。";
            return;
        }

        var userText = Input.Trim();
        if (!string.IsNullOrWhiteSpace(userText))
            _promptHistory.Record(userText);
        var imageViewModels = PendingImages.ToArray();
        var images = imageViewModels.Select(item => item.Content).ToArray();
        PendingImages.Clear();
        OnPropertyChanged(nameof(HasPendingImages));
        Input = string.Empty;
        Messages.Add(new AiChatMessageViewModel("你", userText, imageViewModels));
        var assistant = new AiChatMessageViewModel("AI", string.Empty);
        Messages.Add(assistant);
        OnPropertyChanged(nameof(IsEmpty));
        ToolCalls.Clear();
        IsBusy = true;
        StatusText = "正在生成…";
        var sendGeneration = ++_sendGeneration;
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeSendDetached = detached;
        var toolCards = new Dictionary<string, AiToolCallViewModel>(StringComparer.Ordinal);
        var acceptProgress = true;
        var progress = new Progress<AgentRunEvent>(item =>
        {
            if (!acceptProgress || sendGeneration != _sendGeneration)
                return;
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
                        completedCard.Output = FormatToolOutput(item.ToolResult?.Content);
                        completedCard.Source = item.ToolResult?.Source ?? string.Empty;
                        completedCard.DurationText = $"{Math.Max(0, (DateTimeOffset.UtcNow - completedCard.StartedAtUtc).TotalMilliseconds):F0} ms";
                    }
                    break;
                case AgentRunEventKind.ContextCompacted:
                    StatusText = item.Text ?? "已自动压缩较早的会话历史。";
                    break;
                case AgentRunEventKind.ContextUsageUpdated:
                    if (item.ContextUsage is { } contextUsage)
                        ApplyContextUsage(contextUsage);
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
            var runTask = _session.RunAsync(
                userText,
                SelectedProfile,
                CreateEnabledToolSnapshot(),
                new AgentRunOptions(
                    SupportsStreaming: capabilities.SupportsStreaming,
                    SupportsTools: capabilities.SupportsTools,
                    SupportsStreamingTools: capabilities.SupportsStreamingTools,
                    UseMemory: _connections.Settings.Memory.Enabled),
                progress,
                images: images);
            if (await Task.WhenAny(runTask, detached.Task) != runTask)
            {
                assistant.IsThinking = false;
                if (string.IsNullOrWhiteSpace(assistant.Content))
                    assistant.Content = "（已强制停止，本轮未写入会话历史）";
                _ = ObserveDetachedRunAsync(runTask);
                return;
            }
            var result = await runTask;
            acceptProgress = false;
            if (sendGeneration != _sendGeneration)
                return;
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
            acceptProgress = false;
            if (sendGeneration == _sendGeneration)
            {
                _activeSendDetached = null;
                IsBusy = false;
            }
        }
    }

    public bool TryNavigateInputHistory(bool previous, string currentInput)
    {
        string value;
        var changed = previous
            ? _promptHistory.TryMovePrevious(currentInput, out value)
            : _promptHistory.TryMoveNext(currentInput, out value);
        if (!changed)
            return false;

        _applyingPromptHistory = true;
        try
        {
            Input = value;
        }
        finally
        {
            _applyingPromptHistory = false;
        }
        return true;
    }

    [RelayCommand]
    private async Task CopyMessage(AiChatMessageViewModel? message)
    {
        if (message is not { HasUserActions: true }
            || !await CopyStringToClipboardAsync(message.Content))
        {
            return;
        }

        ToastManager?.Show("用户消息已复制");
    }

    [RelayCommand]
    private void FillMessageInput(AiChatMessageViewModel? message)
    {
        if (message is not { HasUserActions: true })
            return;

        Input = message.Content;
        InputFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task AddImages()
    {
        if (!CanAttachImages)
        {
            StatusText = ImageInputHint;
            return;
        }
        var storageProvider = TopLevel.GetTopLevel(View)?.StorageProvider;
        if (storageProvider is null)
        {
            StatusText = "当前文件选择器不可用。";
            return;
        }
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加图片",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp"],
                },
            ],
        });
        await AddStorageFilesAsync(files);
    }

    [RelayCommand]
    private void RemovePendingImage(AiImageAttachmentViewModel? image)
    {
        if (image is null || !PendingImages.Remove(image))
            return;
        image.Dispose();
        OnPropertyChanged(nameof(HasPendingImages));
        StatusText = PendingImages.Count == 0 ? "已移除全部待发送图片。" : $"待发送 {PendingImages.Count} 张图片。";
    }

    public Task AddClipboardBitmapAsync(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!CanAttachImages)
        {
            StatusText = ImageInputHint;
            return Task.CompletedTask;
        }
        try
        {
            AddPendingImage(AiImageAttachmentProcessor.FromBitmap(bitmap, "clipboard.png"));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            StatusText = $"无法添加剪贴板图片：{exception.Message}";
        }
        return Task.CompletedTask;
    }

    public void ReportImagePasteFailure(string message) => StatusText = $"读取剪贴板失败：{message}";

    public async Task<bool> AddStorageFilesAsync(IEnumerable<IStorageItem>? storageItems)
    {
        if (storageItems is null)
            return false;
        var files = storageItems.OfType<IStorageFile>().ToArray();
        if (files.Length == 0)
            return false;
        if (!CanAttachImages)
        {
            StatusText = ImageInputHint;
            return true;
        }
        foreach (var file in files)
        {
            if (PendingImages.Count >= AiImageAttachmentProcessor.MaximumImageCount)
            {
                StatusText = $"每条消息最多添加 {AiImageAttachmentProcessor.MaximumImageCount} 张图片。";
                break;
            }
            try
            {
                await using var stream = await file.OpenReadAsync();
                var content = await AiImageAttachmentProcessor.FromStreamAsync(stream, file.Name);
                AddPendingImage(content);
            }
            catch (Exception exception) when (exception is InvalidDataException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException)
            {
                StatusText = $"无法添加图片 {file.Name}：{exception.Message}";
                return true;
            }
        }
        return true;
    }

    private void AddPendingImage(AgentImageContent content)
    {
        if (PendingImages.Count >= AiImageAttachmentProcessor.MaximumImageCount)
            throw new InvalidDataException($"每条消息最多添加 {AiImageAttachmentProcessor.MaximumImageCount} 张图片。");
        var totalBytes = PendingImages.Sum(item => item.Content.Data.LongLength) + content.Data.LongLength;
        if (totalBytes > AiImageAttachmentProcessor.MaximumTotalBytes)
            throw new InvalidDataException("每条消息的图片总大小不能超过 20 MiB。");
        PendingImages.Add(new AiImageAttachmentViewModel(content));
        OnPropertyChanged(nameof(HasPendingImages));
        StatusText = $"已添加 {PendingImages.Count} 张图片，可继续输入文字或直接发送。";
    }

    private void ClearPendingImages()
    {
        foreach (var image in PendingImages)
            image.Dispose();
        PendingImages.Clear();
        OnPropertyChanged(nameof(HasPendingImages));
    }

    private void ClearMessages()
    {
        foreach (var message in Messages)
        {
            foreach (var image in message.Images)
                image.Dispose();
        }
        Messages.Clear();
    }

    [RelayCommand]
    private async Task Stop()
    {
        if (_session.Status == AgentSessionStatus.Running)
        {
            _session.Cancel();
            StatusText = "正在停止…若请求未结束，可再次点击强制停止。";
            return;
        }
        if (_session.Status == AgentSessionStatus.Cancelling)
        {
            StatusText = "正在强制停止并重置模型连接…";
            if (await _session.ForceCancelAsync())
            {
                _sendGeneration++;
                var detached = _activeSendDetached;
                _activeSendDetached = null;
                detached?.TrySetResult();
                IsBusy = false;
                StatusText = "已强制停止，可以继续发送。";
            }
            return;
        }
        StatusText = "当前没有正在运行的请求。";
    }

    private static async Task ObserveDetachedRunAsync(Task<AgentRunResult> runTask)
    {
        try
        {
            await runTask;
        }
        catch
        {
            // 强制停止后的旧运行只负责后台清理，不再影响当前页面。
        }
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
    private async Task LoadConversation()
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
        var restoredMessages = new List<AgentMessage>(record.Messages.Count);
        var restoredViewModels = new List<AiChatMessageViewModel>(record.Messages.Count);
        try
        {
            foreach (var message in record.Messages)
            {
                var images = await _conversationStore.LoadAttachmentsAsync(message);
                var imageViewModels = images.Select(image => new AiImageAttachmentViewModel(image)).ToArray();
                restoredViewModels.Add(new AiChatMessageViewModel(
                    message.Role == "user" ? "你" : "AI",
                    message.Content,
                    imageViewModels));
                restoredMessages.Add(message.Role == "user"
                    ? AgentMessage.User(message.Content, images)
                    : AgentMessage.Assistant(message.Content));
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            foreach (var message in restoredViewModels)
            {
                foreach (var image in message.Images)
                    image.Dispose();
            }
            StatusText = $"会话附件读取失败：{exception.Message}";
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
        ClearMessages();
        ClearPendingImages();
        foreach (var message in restoredViewModels)
            Messages.Add(message);
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
            restoredMessages,
            record.ContextSummary,
            record.ContextCompactionCount);
        ResetContextUsage(profile);
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var touched = await _conversationStore.TouchAsync(record.Id);
            if (touched is not null)
                await LoadConversationListAsync(record.Id);
            StatusText = $"已恢复会话：{record.Title}";
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            StatusText = $"已恢复会话：{record.Title}，但更新最后活动时间失败：{exception.Message}";
        }
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
        OnPropertyChanged(nameof(CanAttachImages));
        OnPropertyChanged(nameof(ImageInputHint));
    }

    private bool TryGetCapabilities(
        AiConnectionProfile profile,
        out AiConnectionCapabilities capabilities)
        => _connections.Capabilities.TryGetValue(profile.Id, out capabilities!);

    private AgentToolSnapshot CreateEnabledToolSnapshot()
    {
        var settings = _connections.Settings.EnabledTools;
        return _tools.CreateSnapshot(descriptor =>
            AgentToolPermissionPolicy.IsEnabled(descriptor, settings)
            && (descriptor.PermissionGroup != "AgentMemoryWrite"
                || _connections.Settings.Memory.Enabled));
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

    internal static string FormatToolOutput(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;
        if (content.Length > 64 * 1024)
            return content;
        return AgentJson.Normalize(content, writeIndented: true);
    }

    private void BeginNewSession()
    {
        _session.NewSession();
        _conversationId = Guid.NewGuid();
        _conversationCreatedAtUtc = DateTimeOffset.UtcNow;
        SelectedConversation = null;
        ClearMessages();
        ToolCalls.Clear();
        ClearPendingImages();
        ResetContextUsage(SelectedProfile);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyContextUsage(AgentContextUsage usage)
    {
        var prefix = usage.IsEstimated ? "≈" : string.Empty;
        var percent = usage.UtilizationPercent;
        ContextUsageText = $"上下文 {prefix}{FormatTokenCount(usage.UsedTokens)} / "
                           + $"{FormatTokenCount(usage.ContextWindowTokens)} · {percent:F0}%";
        ContextUsageToolTip = $"输入上下文：{usage.InputTokens.ToString("N0", CultureInfo.InvariantCulture)} Token"
                              + (usage.IsEstimated ? "（本地估算值）" : "（服务端实际值）")
                              + $"\n输出预留：{usage.ReservedOutputTokens.ToString("N0", CultureInfo.InvariantCulture)} Token"
                              + $"\n窗口占用：{usage.UsedTokens.ToString("N0", CultureInfo.InvariantCulture)} Token"
                              + $"\n上下文窗口：{usage.ContextWindowTokens.ToString("N0", CultureInfo.InvariantCulture)} Token"
                              + $"\n自动压缩阈值：{usage.CompressionThresholdTokens.ToString("N0", CultureInfo.InvariantCulture)} Token"
                              + "\n总窗口来自当前连接配置；兼容模型服务未返回 usage 时使用本地估算。";
        ContextUsageBrush = percent >= 90
            ? ContextCriticalBrush
            : percent >= 75
                ? ContextWarningBrush
                : ContextNormalBrush;
        IsContextUsageVisible = true;
    }

    private void ResetContextUsage(AiConnectionProfile? profile)
    {
        IsContextUsageVisible = profile is not null;
        ContextUsageBrush = ContextNormalBrush;
        if (profile is null)
        {
            ContextUsageText = "上下文 —";
            ContextUsageToolTip = "选择模型连接后显示上下文窗口。";
            return;
        }
        ContextUsageText = $"上下文 — / {FormatTokenCount(profile.ContextWindowTokens)}";
        ContextUsageToolTip = $"上下文窗口：{profile.ContextWindowTokens.ToString("N0", CultureInfo.InvariantCulture)} Token"
                              + "\n发送消息后显示最新一次模型请求的上下文占用。"
                              + "\n总窗口来自当前连接配置。";
    }

    internal static string FormatTokenCount(long tokens)
    {
        if (tokens >= 1_048_576 && tokens % 1_048_576 == 0)
            return $"{tokens / 1_048_576d:0.#}M";
        if (tokens >= 1_000_000)
            return $"{tokens / 1_000_000d:0.#}M";
        if (tokens >= 1_024 && tokens % 1_024 == 0)
            return $"{tokens / 1_024d:0.#}K";
        if (tokens >= 1_000)
            return $"{tokens / 1_000d:0.#}K";
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    private async Task SaveConversationAsync(AgentRunResult result, AiConnectionProfile profile)
    {
        var messages = await _conversationStore.CreatePersistedMessagesAsync(_conversationId, _session.Messages);
        var firstUser = Messages.FirstOrDefault(message => message.Role == "你")?.Content
            ?? messages.FirstOrDefault(message => message.Role == "user")?.Content
            ?? "新会话";
        if (string.IsNullOrWhiteSpace(firstUser))
        {
            firstUser = messages.FirstOrDefault(message => message.Role == "user")?
                .Attachments?.FirstOrDefault()?.FileName ?? "图片会话";
        }
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

    private async Task InitializeConversationsAsync()
    {
        try
        {
            await LoadConversationListAsync(selectMostRecent: true);
            if (SelectedConversation is not null)
                await LoadConversation();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            StatusText = $"历史会话初始化失败：{exception.Message}";
        }
    }

    private async Task LoadConversationListAsync(
        Guid? preferredSelectionId = null,
        bool selectMostRecent = false)
    {
        var records = await _conversationStore.LoadAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selectedId = preferredSelectionId ?? SelectedConversation?.Record.Id;
            Conversations.Clear();
            foreach (var record in records)
                Conversations.Add(new AiConversationItemViewModel(record));
            SelectedConversation = Conversations.FirstOrDefault(item => item.Record.Id == selectedId)
                                   ?? (selectMostRecent ? Conversations.FirstOrDefault() : null);
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
