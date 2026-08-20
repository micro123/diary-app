using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.ScriptHost;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed partial class ExportTemplateItemViewModel : ObservableObject
{
    public ExportTemplateDescriptor Descriptor { get; }

    [ObservableProperty]
    private bool _isEnabled = true;

    public string TemplateId => Descriptor.TemplateId;
    public string Version => Descriptor.TemplateVersion;
    public string DisplayName => Descriptor.DisplayName;
    public string Extension => Descriptor.TemplateFileExtension;
    public string PluginId => Descriptor.PluginId;
    public string BindingSummary => Descriptor.Bindings.Count == 0
        ? "无绑定数据"
        : string.Join("、", Descriptor.Bindings.Select(binding =>
            binding.HasDefaultValue ? $"{binding.Key}（默认）" : binding.Key));

    public ExportTemplateItemViewModel(ExportTemplateDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public ExportTemplateItemViewModel(ExportTemplateCatalogEntry entry)
        : this(entry.Descriptor)
    {
        IsEnabled = entry.Enabled;
    }
}

[DiAutoRegister]
public partial class ExportTemplateManagerViewModel : ViewModelBase, IDialogContext
{
    private readonly IExportTemplateCatalog _catalog;

    [ObservableProperty]
    private ObservableCollection<ExportTemplateItemViewModel> _templates = new();

    [ObservableProperty]
    private string _status = string.Empty;

    public ExportTemplateManagerViewModel(IExportTemplateCatalog catalog)
    {
        _catalog = catalog;
        Refresh();
    }

    public bool IsEmpty => Templates.Count == 0;

    public event EventHandler<object?>? RequestClose;

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand]
    private async Task ImportAsync()
    {
        var storageProvider = TopLevel.GetTopLevel(App.Instance.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null)?.StorageProvider;
        if (storageProvider is null)
        {
            Status = "当前没有可用的文件选择器。";
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入数据模板",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("数据模板") { Patterns = ["*.xlsx", "*.docx", "*.csv"] },
                new FilePickerFileType("所有文件") { Patterns = ["*.*"] },
            ],
        });
        var file = files.FirstOrDefault();
        if (file is null)
            return;

        try
        {
            var result = await _catalog.ImportAsync(file.Path.LocalPath);
            if (!result.Succeeded)
            {
                Status = result.ErrorMessage
                    ?? string.Join(" ", result.Diagnostics?.Select(item => item.Message) ?? []);
                NotificationManager?.Show($"模板导入失败：{Status}", NotificationType.Error);
                return;
            }

            Refresh();
            Status = $"已导入：{result.Descriptor!.TemplateId}";
            NotificationManager?.Show($"模板导入成功：{Status}", NotificationType.Success);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            NotificationManager?.Show($"模板导入失败：{Status}", NotificationType.Error);
        }
    }

    [RelayCommand]
    private async Task RevalidateAsync(ExportTemplateItemViewModel item)
    {
        var result = await _catalog.RevalidateAsync(item.TemplateId, item.Version);
        if (!result.Succeeded)
        {
            Status = result.ErrorMessage
                ?? string.Join(" ", result.Diagnostics?.Select(diagnostic => diagnostic.Message) ?? []);
            NotificationManager?.Show($"模板校验失败：{Status}", NotificationType.Error);
            Refresh();
            return;
        }

        Status = $"模板校验通过：{item.TemplateId}";
        Refresh();
    }

    [RelayCommand]
    private void Toggle(ExportTemplateItemViewModel item)
    {
        if (_catalog.SetEnabled(item.TemplateId, item.Version, !item.IsEnabled))
        {
            item.IsEnabled = !item.IsEnabled;
            Status = item.IsEnabled ? "模板已启用。" : "模板已禁用。";
        }
    }

    [RelayCommand]
    private void Archive(ExportTemplateItemViewModel item)
    {
        if (_catalog.Archive(item.TemplateId, item.Version))
        {
            Templates.Remove(item);
            Status = "模板已归档。";
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    private void Refresh()
    {
        Templates = new ObservableCollection<ExportTemplateItemViewModel>(
            _catalog.ListAll().Select(entry => new ExportTemplateItemViewModel(entry)));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
