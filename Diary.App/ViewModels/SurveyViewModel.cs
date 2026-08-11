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
using Diary.App.Utils;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Survey;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

public sealed partial class SurveyResult
{
    private readonly RespondData _data;

    public SurveyResult(RespondData data, double total)
    {
        _data = data;
        GridSource = new HierarchicalTreeDataGridSource<RespondTag>(data.Tags)
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

        UpdatePercent(total > 0 ? total : _data.TotalTime);
        GridSource.ExpandAll();
    }

    public string Title => $"{_data.Username}@{_data.Hostname}";
    public string Range => $"{_data.DateStart} ~ {_data.DateEnd}";
    public double Total => _data.TotalTime;
    public int RecordCount => _data.RecordCount;
    public HierarchicalTreeDataGridSource<RespondTag> GridSource { get; init; }

    private void UpdatePercent(double total)
    {
        if (total <= 0)
        {
            foreach (var tag in _data.Tags)
            {
                tag.Percent = 0;
                foreach (var subTag in tag.SubTags)
                    subTag.Percent = 0;
            }
            return;
        }

        foreach (var tag in _data.Tags)
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

[DiAutoRegister(singleton: true)]
public partial class SurveyViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty] private DateTime _startDate = DateTime.Now.Date;
    [ObservableProperty] private DateTime _endDate = DateTime.Now.Date;
    [ObservableProperty] private double _customTotal = 0;
    [ObservableProperty] private ObservableCollection<SurveyResult> _surveyResults = new();
    [ObservableProperty] private bool _surveying = false;
    [ObservableProperty] private string _extendedText = string.Empty;
    [ObservableProperty] private string _extendedTagNames = string.Empty;
    [ObservableProperty] private int _extendedTagFilterIndex;
    [ObservableProperty] private int _extendedPriorityIndex;
    [ObservableProperty] private string _queryMode = "兼容查询：支持旧版和新版调查节点";
    private object _lock = new();

    public IReadOnlyList<string> ExtendedTagFilters { get; } = ["忽略标签", "任意标签", "全部标签", "无标签", "精确匹配"];
    public IReadOnlyList<string> ExtendedPriorities { get; } = ["全部优先级", .. Enum.GetNames<WorkPriorities>()];

    private IDictionary<string, RespondData> _respondDatas = new Dictionary<string, RespondData>();

    private DbInterfaceBase? Db => App.Instance.UseDb;

    public SurveyViewModel(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

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
        }
    }

    private void StoreExtendedData(string content)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ExtendedSurveyResponse>(content);
            if (response is null || !response.Ok || response.Data is null)
            {
                _logger.LogWarning("扩展调查失败：{Error}", response?.Error ?? "响应无效");
                return;
            }
            StoreData(response.Data.Value.Deserialize<RespondData>());
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "扩展调查响应解析失败");
        }
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
        Dispatcher.UIThread.InvokeAsync(UpdateTree);
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

                var data = SurveyStatisticsBuilder.Build(db, query);
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

    [RelayCommand]
    private async Task SendQuery()
    {
        Surveying = true;
        lock (_lock)
        {
            _respondDatas.Clear();
        }
        ReCalc();
        if (HasExtendedQuery)
        {
            QueryMode = "扩展查询：仅新版调查节点";
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
            };
            EventDispatcher.Msg(new ExtendedSurveyQueryEvent(ExtendedSurveyProtocol.SerializeRequest(request)));
        }
        else
        {
            QueryMode = "兼容查询：支持旧版和新版调查节点";
            EventDispatcher.Msg(new SurveyQueryEvent($"{TimeTools.FormatDateTime(StartDate)}:{TimeTools.FormatDateTime(EndDate)}"));
        }
        await Task.Delay(3000);
        ReCalc();
        Surveying = false;
    }

    private bool HasExtendedQuery => !string.IsNullOrWhiteSpace(ExtendedText)
        || !string.IsNullOrWhiteSpace(ExtendedTagNames)
        || ExtendedTagFilterIndex != 0
        || ExtendedPriorityIndex != 0;

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
