using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Models;
using Diary.App.ViewModels.Dialogs;
using Diary.App.Utils;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Survey;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Ursa.Controls;

namespace Diary.App.ViewModels;

public sealed partial class SurveyResult
{
    private readonly RespondData _data;

    public SurveyResult(RespondData data, double total)
    {
        _data = data;
        var summaryTags = data.Groups.Count > 0
            && data.GroupBy != ExtendedSurveyProtocol.GroupByTag
            ? data.Groups.Select(group => new RespondTag
            {
                TagName = group.Name,
                TagTime = group.TotalTime,
            }).ToList()
            : data.Tags;
        GridSource = new HierarchicalTreeDataGridSource<RespondTag>(summaryTags)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<RespondTag>(
                    new TemplateColumn<RespondTag>(
                        "标签",
                        "NameCell",
                        width: new GridLength(1, GridUnitType.Star),
                        options: new TemplateColumnOptions<RespondTag>
                        {
                            CanUserResizeColumn = false, CanUserSortColumn = false,
                            BeginEditGestures = BeginEditGestures.None
                        }
                    ),
                    x => x.SubTags,
                    x => x.SubTags.Count > 0),
                new TemplateColumn<RespondTag>(
                    "耗时",
                    "TimeCell",
                    width: new GridLength(120, GridUnitType.Pixel),
                    options: new TemplateColumnOptions<RespondTag>
                    {
                        CanUserResizeColumn = false, CanUserSortColumn = false,
                        BeginEditGestures = BeginEditGestures.None
                    }
                ),
                new TemplateColumn<RespondTag>(
                    "占比",
                    "PercentCell",
                    width: new GridLength(120, GridUnitType.Pixel),
                    options: new TemplateColumnOptions<RespondTag>
                    {
                        CanUserResizeColumn = false, CanUserSortColumn = false,
                        BeginEditGestures = BeginEditGestures.None
                    }
                ),
            },
        };

        UpdatePercent(summaryTags, total > 0 ? total : _data.TotalTime);
        GridSource.CollapseAll();
    }

    public string Title => $"{_data.Username}@{_data.Hostname}";
    public string Range => $"{_data.DateStart} ~ {_data.DateEnd}";
    public double Total => _data.TotalTime;
    public int RecordCount => _data.RecordCount;
    public string GroupLabel => _data.GroupBy switch
    {
        ExtendedSurveyProtocol.GroupByDate => "按日期分组",
        ExtendedSurveyProtocol.GroupByPriority => "按优先级分组",
        _ => "按标签分组",
    };
    public IReadOnlyList<RespondDetail> Details => _data.Details;
    public bool HasDetails => _data.Details.Count > 0;
    public string DetailsHeader => _data.DetailsTruncated
        ? $"结果明细（前 {Details.Count} 条，已截断）"
        : $"结果明细（{Details.Count} 条）";
    public HierarchicalTreeDataGridSource<RespondTag> GridSource { get; init; }

    private static void UpdatePercent(IReadOnlyCollection<RespondTag> tags, double total)
    {
        if (total <= 0)
        {
            foreach (var tag in tags)
            {
                tag.Percent = 0;
                foreach (var subTag in tag.SubTags)
                    subTag.Percent = 0;
            }
            return;
        }

        foreach (var tag in tags)
        {
            tag.Percent = tag.TagTime / total * 100.0;
            foreach (var subTag in tag.SubTags)
            {
                subTag.Percent = subTag.TagTime / total * 100.0;
            }
        }
    }

    [RelayCommand]
    private void ToggleExpand(TappedEventArgs args)
    {
        if (UiUtility.TreeDataGridToggleExpand(args.Source as Control))
        {
            args.Handled = true;
        }
    }
}

public sealed class SurveyCapabilityResult
{
    public SurveyCapabilityResult(ExtendedSurveyCapabilities capabilities)
    {
        Hostname = capabilities.Hostname;
        Username = capabilities.Username;
        Kinds = capabilities.Kinds;
        GroupDimensions = capabilities.GroupDimensions;
        SupportsDetails = capabilities.SupportsDetails;
    }

    public string Hostname { get; }
    public string Username { get; }
    public string NodeName => $"{Username}@{Hostname}";
    public IReadOnlyList<string> Kinds { get; }
    public IReadOnlyList<string> GroupDimensions { get; }
    public bool SupportsDetails { get; }
    public string KindsText => string.Join("、", Kinds.Select(FormatKind));
    public string GroupDimensionsText => string.Join("、", GroupDimensions.Select(FormatGroupDimension));
    public string DetailsText => SupportsDetails ? "支持明细" : "仅支持汇总";

    private static string FormatKind(string kind) => kind switch
    {
        ExtendedSurveyProtocol.CapabilitiesKind => "能力发现",
        ExtendedSurveyProtocol.CustomStatisticsKind => "扩展统计",
        _ => kind,
    };

    private static string FormatGroupDimension(string dimension) => dimension switch
    {
        ExtendedSurveyProtocol.GroupByTag => "标签",
        ExtendedSurveyProtocol.GroupByDate => "日期",
        ExtendedSurveyProtocol.GroupByPriority => "优先级",
        _ => dimension,
    };
}

[DiAutoRegister(singleton: true)]
public partial class SurveyViewModel : ViewModelBase
{
    public override bool IsViewCacheable => true;

    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDateRangeValid))]
    [NotifyPropertyChangedFor(nameof(QueryValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(SendQueryCommand))]
    private DateTime _startDate = DateTime.Now.Date;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDateRangeValid))]
    [NotifyPropertyChangedFor(nameof(QueryValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(SendQueryCommand))]
    private DateTime _endDate = DateTime.Now.Date;

    [ObservableProperty] private double _customTotal = 0;
    [ObservableProperty] private ObservableCollection<SurveyResult> _surveyResults = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendQueryCommand))]
    private bool _surveying = false;
    [ObservableProperty] private string _extendedText = string.Empty;
    [ObservableProperty] private string _extendedTagNames = string.Empty;
    [ObservableProperty] private int _extendedTagFilterIndex;
    [ObservableProperty] private int _extendedPriorityIndex;
    [ObservableProperty] private int _extendedGroupByIndex;
    [ObservableProperty] private bool _extendedIncludeDetails;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExtendedQuery))]
    [NotifyPropertyChangedFor(nameof(QueryModeDescription))]
    private int _queryModeIndex;
    [ObservableProperty] private string _queryStatus = "尚未发起调查";
    [ObservableProperty] private ObservableCollection<SurveyCapabilityResult> _peerCapabilities = new();
    [ObservableProperty] private string _capabilityStatus = "尚未探测新版节点能力";
    private readonly object _lock = new();
    private readonly List<string> _queryErrors = new();

    public IReadOnlyList<string> QueryModes { get; } =
        ["兼容查询（v1，支持旧版和新版）", "扩展查询（v2，仅新版）"];
    public IReadOnlyList<string> ExtendedTagFilters { get; } = ["忽略标签", "任意标签", "全部标签", "无标签", "精确匹配"];
    public IReadOnlyList<string> ExtendedPriorities { get; } = ["全部优先级", .. Enum.GetNames<WorkPriorities>()];
    public IReadOnlyList<string> ExtendedGroupDimensions { get; } = ["标签", "日期", "优先级"];
    public bool IsExtendedQuery => QueryModeIndex == 1;
    public bool CanViewCapabilities => PeerCapabilities.Count > 0;
    public bool IsDateRangeValid => StartDate.Date <= EndDate.Date;
    public string QueryValidationMessage => IsDateRangeValid ? string.Empty : "开始日期不能晚于结束日期";
    private bool CanSendQuery => !Surveying && IsDateRangeValid;
    public bool HasQueryErrors
    {
        get
        {
            lock (_lock)
            {
                return _queryErrors.Count > 0;
            }
        }
    }

    public string QueryModeDescription => IsExtendedQuery
        ? "使用 9722，可设置筛选、分组和明细，只返回新版节点。"
        : "使用 9721，只按日期查询，兼容旧版和新版节点。";

    private IDictionary<string, RespondData> _respondDatas = new Dictionary<string, RespondData>();

    private DbInterfaceBase? Db => App.Instance.UseDb;

    public SurveyViewModel(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        PeerCapabilities.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanViewCapabilities));
            ViewCapabilitiesCommand.NotifyCanExecuteChanged();
        };

        Messenger.Register<SurveyRequestEvent>(this, (r, m) => CollectData(m.Value));
        Messenger.Register<RespondEvent>(this, (r, m) => StoreData(m.Value));
        Messenger.Register<ExtendedSurveyRequestEvent>(this, (r, m) => CollectExtendedData(m.Value));
        Messenger.Register<ExtendedRespondEvent>(this, (r, m) => StoreExtendedData(m.Value));
        Messenger.Register<QuickSurveyEvent>(this, (r, m) => BuildRange(m.Value.Item1, m.Value.Item2));
    }

    private void BuildRange(DateTime start, AdjustPart part)
    {
        StartDate = start;
        MakeRange(part, AdjustDirection.Current);
        SendQueryCommand.ExecuteAsync(null);
    }

    private void StoreData(string content)
    {
        try
        {
            var data = JsonSerializer.Deserialize<RespondData>(content);
            StoreData(data);
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, exception.Message);
            AddQueryError("收到无法解析的兼容调查响应");
        }
    }

    private void StoreExtendedData(string content)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ExtendedSurveyResponse>(content);
            if (response is null || !response.Ok || response.Data is null)
            {
                var error = response?.Error ?? "响应无效";
                _logger.LogWarning("扩展调查失败：{Error}", error);
                AddQueryError(error);
                return;
            }

            if (response.Data.Value.TryGetProperty("kind", out var kind)
                && kind.GetString() == ExtendedSurveyProtocol.CapabilitiesKind)
            {
                var capabilities = response.Data.Value.Deserialize<ExtendedSurveyCapabilities>();
                if (capabilities is null || string.IsNullOrWhiteSpace(capabilities.Hostname))
                    return;

                Dispatcher.UIThread.Post(() => StoreCapabilities(capabilities));
                return;
            }

            StoreData(response.Data.Value.Deserialize<RespondData>());
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "扩展调查响应解析失败");
            AddQueryError("收到无法解析的扩展调查响应");
        }
    }

    private void StoreCapabilities(ExtendedSurveyCapabilities capabilities)
    {
        var result = new SurveyCapabilityResult(capabilities);
        var existing = PeerCapabilities
            .Select((value, index) => (value, index))
            .FirstOrDefault(item => item.value.NodeName == result.NodeName);
        if (existing.value is not null)
            PeerCapabilities[existing.index] = result;
        else
            PeerCapabilities.Add(result);
        CapabilityStatus = $"已发现 {PeerCapabilities.Count} 个新版节点";
    }

    private void StoreData(RespondData? data)
    {
        if (data is null)
            return;
        if (data.Tags.Count == 0)
            data.Tags.Add(RespondTag.Null);
        lock (_lock)
        {
            _respondDatas[data.Key] = data;
        }
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpdateTree();
            UpdateQueryStatus();
        });
    }

    private void UpdateTree()
    {
        SurveyResults.Clear();
        lock (_lock)
        {
            foreach (var (_, v) in _respondDatas)
            {
                SurveyResults.Add(new SurveyResult(v, CustomTotal));
            }
        }
    }

    private void AddQueryError(string error)
    {
        lock (_lock)
        {
            if (!_queryErrors.Contains(error, StringComparer.Ordinal))
                _queryErrors.Add(error);
        }
        Dispatcher.UIThread.Post(UpdateQueryStatus);
    }

    private void UpdateQueryStatus()
    {
        int resultCount;
        string[] errors;
        lock (_lock)
        {
            resultCount = _respondDatas.Count;
            errors = _queryErrors.ToArray();
        }

        var phase = Surveying ? "正在调查" : "调查结束";
        QueryStatus = errors.Length == 0
            ? $"{phase}：已收到 {resultCount} 个节点结果"
            : $"{phase}：已收到 {resultCount} 个节点结果；节点错误：{string.Join("；", errors)}";
        OnPropertyChanged(nameof(HasQueryErrors));
    }


    private void CollectData(string query)
    {
        _logger.LogDebug("received query: {Query}", query);
        var parts = query.Split(':');
        if (parts.Length != 2)
        {
            _logger.LogError("invalid query: {Query}", query);
        }
        else
        {
            Task.Run(() =>
            {
                try
                {
                    var db = Db;
                    if (db is null)
                    {
                        _logger.LogWarning("Survey 查询被忽略：数据库不可用");
                        return;
                    }
                    var statistics = db.GetStatistics(parts[0], parts[1]);
                    var data = new RespondData
                    {
                        Hostname = SysInfo.GetHostname(),
                        Username = SysInfo.GetUsername(),
                        DateStart = parts[0],
                        DateEnd = parts[1],
                        TotalTime = statistics.Total,
                    };

                    var sum1 = 0.0;
                    var sum2 = 0.0;
                    foreach (var tagTime in statistics.PrimaryTags)
                    {
                        var primaryTag = new RespondTag
                        {
                            TagName = tagTime.TagName,
                            TagTime = tagTime.Time,
                        };
                        sum2 = 0.0;
                        if (tagTime.Nested.Count > 0)
                        {
                            var list = primaryTag.SubTags;
                            foreach (var nested in tagTime.Nested)
                            {
                                sum2 += nested.Time;
                                list.Add(new RespondTag { TagName = nested.TagName, TagTime = nested.Time });
                            }
                        }

                        if (sum2 < tagTime.Time && primaryTag.SubTags.Count > 0)
                            primaryTag.SubTags.Add(new RespondTag { TagTime = tagTime.Time - sum2 });

                        sum1 += tagTime.Time;
                        data.Tags.Add(primaryTag);
                    }

                    if (sum1 < statistics.Total)
                        data.Tags.Add(new RespondTag { TagTime = statistics.Total - sum1 });

                    var content = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
                    _logger.LogDebug("respond content: {Content}", content);
                    EventDispatcher.Msg(new SurveyResultEvent(content));
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Survey 查询失败");
                }
            });
        }
    }

    private void CollectExtendedData(string content)
    {
        if (!ExtendedSurveyProtocol.TryDeserializeRequest(content, out var request) || request is null)
        {
            _logger.LogWarning("忽略无效扩展调查请求：{Content}", content);
            return;
        }

        if (request.Kind == ExtendedSurveyProtocol.CapabilitiesKind)
        {
            EventDispatcher.Msg(new ExtendedSurveyResultEvent(
                ExtendedSurveyProtocol.SerializeCapabilitiesSuccess(
                    request.RequestId,
                    SysInfo.GetHostname(),
                    SysInfo.GetUsername())));
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var db = Db;
                if (db is null)
                {
                    EventDispatcher.Msg(new ExtendedSurveyResultEvent(
                        ExtendedSurveyProtocol.SerializeError(request.RequestId, "数据库不可用")));
                    return;
                }

                if (!SurveyStatisticsBuilder.TryBuildQuery(db, request, out var query, out var error))
                {
                    EventDispatcher.Msg(new ExtendedSurveyResultEvent(
                        ExtendedSurveyProtocol.SerializeError(request.RequestId, error)));
                    return;
                }

                var data = SurveyStatisticsBuilder.Build(
                    db,
                    query,
                    request.GroupBy,
                    request.IncludeDetails);
                var dataJson = JsonSerializer.Serialize(data);
                EventDispatcher.Msg(new ExtendedSurveyResultEvent(
                    ExtendedSurveyProtocol.SerializeSuccess(request.RequestId, dataJson)));
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "扩展调查查询失败");
                EventDispatcher.Msg(new ExtendedSurveyResultEvent(
                    ExtendedSurveyProtocol.SerializeError(request.RequestId, "扩展调查查询失败")));
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanSendQuery))]
    private async Task SendQuery()
    {
        if (!IsDateRangeValid)
        {
            QueryStatus = $"查询条件无效：{QueryValidationMessage}";
            return;
        }

        Surveying = true;
        lock (_lock)
        {
            _respondDatas.Clear();
            _queryErrors.Clear();
        }
        ReCalc();
        UpdateQueryStatus();
        if (IsExtendedQuery)
        {
            var request = new ExtendedSurveyRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                StartDate = TimeTools.FormatDateTime(StartDate),
                EndDate = TimeTools.FormatDateTime(EndDate),
                Text = string.IsNullOrWhiteSpace(ExtendedText) ? null : ExtendedText.Trim(),
                TagNames = ExtendedTagNames
                    .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                TagFilter = ((WorkItemTagFilter)ExtendedTagFilterIndex).ToString(),
                Priority = ExtendedPriorityIndex == 0 ? null : ExtendedPriorityIndex - 1,
                GroupBy = ExtendedGroupByIndex switch
                {
                    1 => ExtendedSurveyProtocol.GroupByDate,
                    2 => ExtendedSurveyProtocol.GroupByPriority,
                    _ => ExtendedSurveyProtocol.GroupByTag,
                },
                IncludeDetails = ExtendedIncludeDetails,
            };
            EventDispatcher.Msg(new ExtendedSurveyQueryEvent(ExtendedSurveyProtocol.SerializeRequest(request)));
        }
        else
        {
            EventDispatcher.Msg(new SurveyQueryEvent($"{TimeTools.FormatDateTime(StartDate)}:{TimeTools.FormatDateTime(EndDate)}"));
        }
        await Task.Delay(3000);
        Surveying = false;
        ReCalc();
        UpdateQueryStatus();
    }

    [RelayCommand]
    private async Task DiscoverCapabilities()
    {
        PeerCapabilities.Clear();
        CapabilityStatus = "正在探测新版节点能力...";
        EventDispatcher.Msg(new ExtendedSurveyQueryEvent(
            ExtendedSurveyProtocol.SerializeCapabilitiesRequest(Guid.NewGuid().ToString("N"))));

        await Task.Delay(1500);
        if (PeerCapabilities.Count == 0 && CapabilityStatus == "正在探测新版节点能力...")
            CapabilityStatus = "未发现支持 v2 的节点";
    }

    [RelayCommand(CanExecute = nameof(CanViewCapabilities))]
    private async Task ViewCapabilities()
    {
        await OverlayDialog.ShowCustomModal<object>(
            new SurveyCapabilitiesViewModel(PeerCapabilities, CapabilityStatus),
            options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = true,
                CanLightDismiss = true,
                IsCloseButtonVisible = true,
            });
    }

    [RelayCommand]
    private void OpenSurveyGuide()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Docs", "SurveyUserGuide.md");
        try
        {
            ProcUtils.OpenFileCrossPlatform(path);
            QueryStatus = "已打开调查功能使用指南";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "打开调查功能使用指南失败");
            QueryStatus = $"无法打开调查功能使用指南：{exception.Message}";
        }
    }

    [RelayCommand]
    private void ReCalc()
    {
        UpdateTree();
    }

    [RelayCommand]
    private void QuickSelectDate(string which)
    {
        Debug.Assert(which.Length == 3);
        var col = which[1] - '0';
        var row = which[2] - '0';

        MakeRange((AdjustPart)row, (AdjustDirection)col);
    }

    private void MakeRange(AdjustPart row, AdjustDirection col)
    {
        DateTime startDate = StartDate;
        DateTime endDate = EndDate;
        TimeTools.AdjustDate(ref startDate, ref endDate, row, col);
        StartDate = startDate;
        EndDate = endDate;
    }
}
