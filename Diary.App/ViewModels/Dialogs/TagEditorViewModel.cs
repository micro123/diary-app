using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.App.Services;
using Diary.Core.Data.Base;
using Diary.GUIBase.Converters;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Ursa.Controls;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class TagEditorViewModel : ViewModelBase, IDialogContext
{
    private readonly ILogger _logger;
    private readonly TrackerPluginLifecycleCoordinator _lifecycleCoordinator;
    private readonly TagSharePackageService _tagSharePackageService;
    public string Title => "标签编辑器";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewTagCommand))]
    private string _newTagName = string.Empty;
    [ObservableProperty] private bool _newIsPrimary = true;
    [ObservableProperty] private HsvColor _newTagColor = default;

    [ObservableProperty] private ObservableCollection<EditableWorkTag> _allTags = new();
    [ObservableProperty] private EditableWorkTag? _selectedTag;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExportSelectionOpen))]
    private TagShareExportDialogViewModel? _exportSelectionDialog;

    public bool IsExportSelectionOpen => ExportSelectionDialog is not null;
    public ObservableCollection<ITagRuleEditorContribution> RuleContributions { get; } = new();

    private bool _changed;

    public TagEditorViewModel(
        ILogger logger,
        TrackerUiContributionRegistry trackerRegistry,
        TrackerPluginLifecycleCoordinator lifecycleCoordinator,
        TagSharePackageService tagSharePackageService)
    {
        _logger = logger;
        _lifecycleCoordinator = lifecycleCoordinator;
        _tagSharePackageService = tagSharePackageService;
        foreach (var contribution in trackerRegistry.Contributions)
        {
            var ruleContribution = contribution.CreateTagRuleEditorContribution();
            if (ruleContribution is not null)
                RuleContributions.Add(ruleContribution);
        }
        LoadTags();
    }

    partial void OnSelectedTagChanged(EditableWorkTag? value)
    {
        if (value is null)
            return;
        foreach (var contribution in RuleContributions)
            contribution.SelectTag(value.Tag);
    }

    public void Close()
    {
        if (ExportSelectionDialog is not null)
        {
            ExportSelectionDialog.Close();
            return;
        }

        Cancel();
    }

    public event EventHandler<object?>? RequestClose;

    [RelayCommand]
    private void Save() => SaveChanges(close: true);

    private bool SaveChanges(bool close)
    {
        if (!ValidateExtraFieldKeys(out var fieldError))
        {
            EventDispatcher.Notify("错误", fieldError!);
            return false;
        }

        bool changed = _changed;
        foreach (var tag in AllTags)
        {
            var tagChanged = tag.ApplyChanges(out var error);
            if (error is not null)
            {
                EventDispatcher.Notify("错误", error);
                return false;
            }

            changed |= tagChanged;
        }
        if (changed)
            EventDispatcher.DbChanged(DbChangedEvent.WorkTags);
        foreach (var pluginId in RuleContributions.Select(item => item.PluginId).Distinct())
        {
            foreach (var contribution in RuleContributions.Where(item => item.PluginId == pluginId))
                contribution.Commit();
            if (!_lifecycleCoordinator.SaveConfiguration(pluginId))
                _logger.LogWarning("保存标签规则配置失败: {PluginId}", pluginId);
        }
        _changed = false;
        if (close)
            RequestClose?.Invoke(this, null);
        return true;
    }

    [RelayCommand]
    private void Cancel()
    {
        ReloadRules();
        RequestClose?.Invoke(this, null);
    }

    public void ReloadRules()
    {
        foreach (var contribution in RuleContributions)
            contribution.Reload();
    }

    [RelayCommand]
    private async Task ExportTags()
    {
        if (!SaveChanges(close: false))
            return;
        var database = App.Instance.UseDb;
        var storageProvider = GetStorageProvider();
        if (database is null || storageProvider is null)
        {
            EventDispatcher.Notify("错误", "当前数据库或文件选择器不可用。");
            return;
        }

        var tags = database.AllWorkTags();
        if (tags.Count == 0)
        {
            EventDispatcher.Notify("无法导出标签", "当前没有可导出的标签。");
            return;
        }
        var selection = await ShowExportSelectionAsync(tags);
        if (selection is null || selection.TagIds.Count == 0)
            return;
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出标签",
            SuggestedFileName = $"DiaryApp-tags-{DateTime.Now:yyyyMMdd-HHmmss}{TagSharePackageService.FileExtension}",
            DefaultExtension = TagSharePackageService.FileExtension.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType("DiaryApp 标签包")
                {
                    Patterns = [$"*{TagSharePackageService.FileExtension}"],
                },
            ],
        });
        if (file is null)
            return;
        try
        {
            var path = EnsureTagPackageExtension(file.Path.LocalPath);
            await _tagSharePackageService.ExportAsync(
                path,
                database,
                selection.TagIds,
                RuleContributions);
            EventDispatcher.Notify("标签导出完成", $"已导出 {selection.TagIds.Count} 个标签：{path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException)
        {
            _logger.LogError(exception, "导出标签包失败");
            EventDispatcher.Notify("标签导出失败", exception.Message);
        }
    }

    private Task<TagShareExportSelection?> ShowExportSelectionAsync(IEnumerable<WorkTag> tags)
    {
        var dialog = new TagShareExportDialogViewModel();
        dialog.Initialize(tags);
        var completion = new TaskCompletionSource<TagShareExportSelection?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRequestClose(object? sender, object? result)
        {
            dialog.RequestClose -= OnRequestClose;
            ExportSelectionDialog = null;
            completion.TrySetResult(result as TagShareExportSelection);
        }

        dialog.RequestClose += OnRequestClose;
        ExportSelectionDialog = dialog;
        return completion.Task;
    }

    [RelayCommand]
    private async Task ImportTags()
    {
        if (!SaveChanges(close: false))
            return;
        var database = App.Instance.UseDb;
        var storageProvider = GetStorageProvider();
        if (database is null || storageProvider is null)
        {
            EventDispatcher.Notify("错误", "当前数据库或文件选择器不可用。");
            return;
        }
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入标签",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("DiaryApp 标签包")
                {
                    Patterns = [$"*{TagSharePackageService.FileExtension}"],
                },
            ],
        });
        var file = files.FirstOrDefault();
        if (file is null)
            return;
        try
        {
            var preview = await _tagSharePackageService.PreviewImportAsync(file.Path.LocalPath, database);
            var dialog = new TagShareImportDialogViewModel();
            dialog.Initialize(preview, RuleContributions);
            var selection = await OverlayDialog.ShowCustomModal<TagShareImportSelection>(
                dialog,
                options: new OverlayDialogOptions
                {
                    CanDragMove = false,
                    CanResize = false,
                    CanLightDismiss = false,
                    IsCloseButtonVisible = false,
                });
            if (selection is null || selection.TagKeys.Count == 0)
                return;
            var result = _tagSharePackageService.Import(
                preview, database, selection.TagKeys, selection.TrackerMappings);
            foreach (var pluginId in result.ChangedPluginIds)
            {
                if (!_lifecycleCoordinator.SaveConfiguration(pluginId))
                    _logger.LogWarning("保存导入的标签规则配置失败: {PluginId}", pluginId);
            }
            LoadTags();
            ReloadRules();
            EventDispatcher.DbChanged(DbChangedEvent.WorkTags);
            EventDispatcher.Notify(
                "标签导入完成",
                $"新增 {result.Created}，更新 {result.Updated}，重新启用 {result.Enabled}；" +
                $"Tracker 规则导入 {result.Trackers.Sum(item => item.Imported)}，" +
                $"跳过 {result.Trackers.Sum(item => item.Invalid + item.Unavailable + item.Skipped)}。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        {
            _logger.LogError(exception, "导入标签包失败");
            EventDispatcher.Notify("标签导入失败", exception.Message);
        }
    }

    private static IStorageProvider? GetStorageProvider()
        => TopLevel.GetTopLevel(App.Instance.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null)?.StorageProvider;

    private static string EnsureTagPackageExtension(string path)
        => path.EndsWith(TagSharePackageService.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : path + TagSharePackageService.FileExtension;

    [RelayCommand]
    private void DelTag(EditableWorkTag tag)
    {
        if (tag.Delete())
        {
            AllTags.Remove(tag);
            SelectedTag = AllTags.FirstOrDefault();
        }
    }

    [RelayCommand]
    private async Task AddExtraField()
    {
        await EditExtraField(null);
    }

    [RelayCommand]
    private async Task EditExtraField(EditableTagExtraField? field)
    {
        var tag = SelectedTag;
        if (tag is null)
            return;

        var draft = field?.Clone() ?? new EditableTagExtraField(tag.Id);
        var editor = new TagExtraFieldEditorViewModel(draft);
        var accepted = await OverlayDialog.ShowCustomModal<bool>(
            editor,
            options: new OverlayDialogOptions
            {
                Title = editor.Title,
                CanDragMove = false,
                CanResize = false,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
                Mode = DialogMode.None,
            });
        if (!accepted)
            return;

        if (field is null)
            tag.ExtraFields.Add(draft);
        else
            field.CopyFrom(draft);
        _changed = true;
    }

    [RelayCommand(CanExecute = nameof(CanAddTag))]
    private void NewTag()
    {
        _logger.LogInformation("new tag, name = {name}, primary = {primary}, color = {color}", NewTagName, NewIsPrimary, NewTagColor);
        int rgb = HsvColorConverter.FromHsv(NewTagColor);
        var tag = App.Instance.UseDb!.CreateWorkTag(NewTagName, NewIsPrimary, rgb);
        if (tag.Id > 0)
        {
            _changed = true;
            NewTagName = string.Empty;
            LoadTags();
        }
        else
        {
            EventDispatcher.Notify("错误", "添加标签失败了，可能是重复的标签名！");
        }
    }

    private bool CanAddTag() => !string.IsNullOrWhiteSpace(NewTagName);

    private bool ValidateExtraFieldKeys(out string? error)
    {
        var fields = AllTags.SelectMany(tag => tag.ExtraFields);
        var keys = new Dictionary<string, EditableTagExtraField>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!field.Validate(out error))
                return false;

            var normalized = TagExtraFieldKeyRules.Normalize(field.FieldKey.Trim());
            if (!keys.TryAdd(normalized, field))
            {
                error = $"字段标识已存在：{field.FieldKey.Trim()}";
                return false;
            }
        }

        error = null;
        return true;
    }

    private void LoadTags()
    {
        var all = App.Instance.UseDb?.AllWorkTags();
        if (all == null)
            return;
        AllTags.Clear();
        foreach (var tag in all)
            AllTags.Add(new EditableWorkTag(tag));
        SelectedTag = AllTags.FirstOrDefault();
    }
}
