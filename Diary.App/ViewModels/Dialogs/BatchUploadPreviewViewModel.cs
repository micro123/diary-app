using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record BatchUploadSelection(IReadOnlyList<WorkEditorViewModel> Items);

public partial class BatchUploadPreviewItem(WorkEditorViewModel work) : ObservableObject
{
    public WorkEditorViewModel Work { get; } = work;
    public int WorkId => Work.WorkId;
    public string Date => Work.Date;
    public string Title => string.IsNullOrWhiteSpace(Work.Comment) ? "无标题事项" : Work.Comment;
    public double Hours => Work.Time;
    public string StatusText => Work.UploadStatusText;
    public string TrackerText => Work.Extensions.Count == 0
        ? "未配置 Tracker"
        : string.Join(", ", Work.Extensions.Select(extension =>
            $"{extension.Key.PluginId}/{extension.Key.InstanceId}"));
    public bool CanSelect => Work.CanUpload() && Work.UploadStatus != WorkItemUploadStatus.Uncertain;

    [ObservableProperty]
    private bool _selected = work.CanUpload() && work.UploadStatus != WorkItemUploadStatus.Uncertain;
}

[DiAutoRegister]
public partial class BatchUploadPreviewViewModel : ViewModelBase, IDialogContext
{
    public ObservableCollection<BatchUploadPreviewItem> Items { get; } = new();

    public string Summary =>
        $"共 {Items.Count} 条记录，已选择 {Items.Count(item => item.Selected)} 条，共 {Items.Where(item => item.Selected).Sum(item => item.Hours):0.##} 小时。";

    public BatchUploadPreviewViewModel(IEnumerable<WorkEditorViewModel> works)
    {
        foreach (var work in works)
        {
            var item = new BatchUploadPreviewItem(work);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchUploadPreviewItem.Selected))
            OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void Confirm()
    {
        var selected = Items.Where(item => item.Selected && item.CanSelect)
            .Select(item => item.Work)
            .ToArray();
        if (selected.Length > 0)
            RequestClose?.Invoke(this, new BatchUploadSelection(selected));
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, null);

    public event EventHandler<object?>? RequestClose;

    public void Close() => Cancel();
}
