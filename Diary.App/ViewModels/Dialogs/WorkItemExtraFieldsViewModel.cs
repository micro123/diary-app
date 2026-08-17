using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.GUIBase.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public partial class WorkItemExtraFieldsViewModel : ViewModelBase, IDialogContext
{
    private readonly DbInterfaceBase _db;
    private readonly int _workItemId;

    public string Title => "编辑附加信息";
    public ObservableCollection<ExtraFieldGroupViewModel> Groups { get; } = new();
    public bool HasFields => Groups.Any(group => group.Fields.Count > 0);

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public WorkItemExtraFieldsViewModel(
        DbInterfaceBase db,
        int workItemId,
        IReadOnlyCollection<WorkItemExtraField> fields)
    {
        _db = db;
        _workItemId = workItemId;
        foreach (var group in fields.GroupBy(field => new { field.TagId, field.TagName }))
        {
            var target = new ExtraFieldGroupViewModel(group.Key.TagId, group.Key.TagName);
            foreach (var field in group.OrderBy(field => field.SortOrder).ThenBy(field => field.FieldKey))
                target.Fields.Add(new EditableWorkItemExtraField(field));
            Groups.Add(target);
        }
    }

    public IReadOnlyCollection<WorkItemExtraFieldValue> GetValues() =>
        Groups.SelectMany(group => group.Fields)
            .Select(field => new WorkItemExtraFieldValue
            {
                WorkItemId = _workItemId,
                FieldId = field.FieldId,
                Value = field.Value.Trim(),
            })
            .ToArray();

    [RelayCommand]
    private void Save()
    {
        foreach (var field in Groups.SelectMany(group => group.Fields))
        {
            if (!TagExtraFieldValueValidator.TryValidate(field.Type, field.Value, field.Options, out var error))
            {
                ValidationMessage = $"{field.Label}：{error}";
                return;
            }
        }
        ValidationMessage = string.Empty;
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(this, false);
    }

    public void Close() => Cancel();

    public event EventHandler<object?>? RequestClose;
}
