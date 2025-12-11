using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.Database;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

public sealed class SurveyResult
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
                    new TextColumn<RespondTag, string>(
                        "标签",
                        x => x.TagName,
                        (o, v) => o.TagName = v!,
                        new GridLength(1, GridUnitType.Star),
                        new()
                        {
                            StringFormat = "{0}", CanUserResizeColumn = false, CanUserSortColumn = false,
                            BeginEditGestures = BeginEditGestures.None
                        }
                    ),
                    x => x.SubTags,
                    x => x.SubTags.Count > 0),
                new TextColumn<RespondTag, double>(
                    "耗时",
                    x => x.TagTime,
                    (o, v) => o.TagTime = v!,
                    new GridLength(120, GridUnitType.Pixel),
                    new()
                    {
                        StringFormat = "{0:0.##} 小时", CanUserResizeColumn = false, CanUserSortColumn = false,
                        BeginEditGestures = BeginEditGestures.None
                    }
                ),
                new TextColumn<RespondTag, double>(
                    "占比",
                    x => x.Percent,
                    (o, v) => o.Percent = v!,
                    new GridLength(120, GridUnitType.Pixel),
                    new()
                    {
                        StringFormat = "{0:0.##} %", CanUserResizeColumn = false, CanUserSortColumn = false,
                        BeginEditGestures = BeginEditGestures.None
                    }
                ),
            },
        };

        UpdatePercent(total > 0 ? total : _data.TotalTime);
    }

    public string Title => $"{_data.Username}@{_data.Hostname}";
    public string Range => $"{_data.DateStart} ~ {_data.DateEnd}";
    public double Total => _data.TotalTime;
    public ITreeDataGridSource GridSource { get; init; }

    private void UpdatePercent(double total)
    {
        foreach (var tag in _data.Tags)
        {
            tag.Percent = tag.TagTime / total * 100.0;
            foreach (var subTag in tag.SubTags)
            {
                subTag.Percent = subTag.TagTime / total * 100.0;
            }
        }
    }
}

[DiAutoRegister]
public partial class SurveyViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty] private DateTime _startDate = DateTime.Now.Date;
    [ObservableProperty] private DateTime _endDate = DateTime.Now.Date;
    [ObservableProperty] private double _customTotal = 0;
    [ObservableProperty] private ObservableCollection<SurveyResult> _surveyResults = new();
    [ObservableProperty] private bool _surveying = false;
    private object _lock = new();

    private IDictionary<string, RespondData> _respondDatas = new Dictionary<string, RespondData>();

    private DbInterfaceBase? Db => App.Current.UseDb;

    public SurveyViewModel(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        Messenger.Register<SurveyRequestEvent>(this, (r, m) => CollectData(m.Value));
        Messenger.Register<RespondEvent>(this, (r, m) => StoreData(m.Value));
    }

    private void StoreData(string content)
    {
        try
        {
            var data = JsonSerializer.Deserialize<RespondData>(content);
            if (data != null)
            {
                if (data.Tags.Count == 0)
                {
                    data.Tags.Add(RespondTag.Null);
                }
                lock (_lock)
                {
                    if (!_respondDatas.TryAdd(data.Key, data))
                    {
                        _respondDatas[data.Key] = data;
                    }
                }

                Dispatcher.UIThread.InvokeAsync(UpdateTree);
            }
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, exception.Message);
        }
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
        _logger.LogInformation($"received query: {query}");
        var parts = query.Split(':');
        if (parts.Length != 2)
        {
            _logger.LogError($"invalid query: {query}");
        }
        else
        {
            Task.Run(() =>
            {
                var statistics = Db!.GetStatistics(parts[0], parts[1]);
                var data = new RespondData()
                {
                    Hostname = SysInfo.GetHostname(),
                    Username = SysInfo.GetUsername(),
                    DateStart = parts[0],
                    DateEnd = parts[1],
                    TotalTime = statistics.Total,
                };

                foreach (var tagTime in statistics.PrimaryTags)
                {
                    var primaryTag = new RespondTag()
                    {
                        TagName = tagTime.TagName,
                        TagTime = tagTime.Time,
                    };
                    if (tagTime.Nested.Count > 0)
                    {
                        var list = primaryTag.SubTags;
                        foreach (var nested in tagTime.Nested)
                        {
                            list.Add(new RespondTag() { TagName = nested.TagName, TagTime = nested.Time });
                        }
                    }

                    data.Tags.Add(primaryTag);
                }

                var content = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
                _logger.LogInformation($"respond content: {content}");
                EventDispatcher.Msg(new SurveyResultEvent(content));
            });
        }
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
        EventDispatcher.Msg(new SurveyQueryEvent($"{TimeTools.FormatDateTime(StartDate)}:{TimeTools.FormatDateTime(EndDate)}"));
        await Task.Delay(3000);
        ReCalc();
        Surveying = false;
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
        
        DateTime startDate = StartDate;
        DateTime endDate = EndDate;
        TimeTools.AdjustDate(ref startDate, ref endDate, (AdjustPart)row, (AdjustDirection)col);
        StartDate = startDate;
        EndDate = endDate;
    }
}