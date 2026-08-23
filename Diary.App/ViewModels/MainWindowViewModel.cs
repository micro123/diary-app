using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Services;
using Diary.Core;
using Diary.Core.Constants;
using Diary.App.Models;
using Diary.Core.Data.AppConfig;
using Diary.Core.Utils;
using Diary.Database;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Script.Runtime;
using Diary.Update;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ursa.Controls;
using AboutViewModel = Diary.App.ViewModels.Dialogs.AboutViewModel;
using DbMigrationViewModel = Diary.App.ViewModels.Dialogs.DbMigrationViewModel;
using GenericConfigViewModel = Diary.GUIBase.ViewModels.Dialogs.GenericConfigViewModel;
using OnboardingAction = Diary.App.ViewModels.Dialogs.OnboardingAction;
using OnboardingViewModel = Diary.App.ViewModels.Dialogs.OnboardingViewModel;
using ScriptShareImportDialogViewModel = Diary.App.ViewModels.Dialogs.ScriptShareImportDialogViewModel;
using ScriptShareImportSelection = Diary.App.ViewModels.Dialogs.ScriptShareImportSelection;
using StandardMessageView = Diary.App.Views.Dialogs.StandardMessageView;
using StandardMessageViewModel = Diary.App.ViewModels.Dialogs.StandardMessageViewModel;
using TagEditorViewModel = Diary.App.ViewModels.Dialogs.TagEditorViewModel;
using TemplateEditorViewModel = Diary.App.ViewModels.Dialogs.TemplateEditorViewModel;
using TrackerSettingsDialogViewModel = Diary.App.ViewModels.Dialogs.TrackerSettingsDialogViewModel;
using ExportTemplateManagerViewModel = Diary.App.ViewModels.Dialogs.ExportTemplateManagerViewModel;

namespace Diary.App.ViewModels;

[DiAutoRegister(singleton: true)]
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly StatusBarViewModel _statusBarViewModel;
    private readonly IServiceProvider _serviceProvider;
    private readonly TrackerPluginLifecycleCoordinator _lifecycle;
    private readonly UserManualService _userManualService;
    private readonly ILogger _logger;
    private IReadOnlyList<NavigateInfo> _fixedPages;
    public string VersionString => AppInfo.AppVersionString;

    public string VersionDetails => AppInfo.AppVersionDetails;
    public bool IsUserManualVisible => _userManualService.IsMenuVisible;
    public StatusBarViewModel StatusBar => _statusBarViewModel;

    [RelayCommand]
    private async Task CopyVersion(bool simple)
    {
        await CopyStringToClipboardAsync(simple ? VersionString : VersionDetails);
        ToastManager?.Show("已复制", NotificationType.Success);
    }

    [ObservableProperty] private ObservableCollection<NavigateInfo> _pages = new();

    [ObservableProperty] private NavigateInfo? _selectedPage = null;

    partial void OnSelectedPageChanged(NavigateInfo? value)
    {
        CurrentPageModel = value?.ViewModel;
    }

    [ObservableProperty] private ViewModelBase? _currentPageModel = null;

    public MainWindowViewModel(IServiceProvider serviceProvider, ILogger logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _lifecycle = serviceProvider.GetRequiredService<TrackerPluginLifecycleCoordinator>();
        _userManualService = serviceProvider.GetRequiredService<UserManualService>();

        // 导航可扩展：固定核心页面 + tracker 贡献页；设置通过标题栏对话框打开。
        // 手势按最终位置分配 Alt+1..；单 tracker（RedMine）下顺序/手势与原硬编码一致。
        _fixedPages = BuildFixedPages();
        Pages = new ObservableCollection<NavigateInfo>(_fixedPages);
        RefreshTrackerPages();
        _statusBarViewModel = _serviceProvider.GetRequiredService<StatusBarViewModel>();

        SelectedPage = Pages[0];

        Messenger.Register<PageSwitchEvent>(this, (r, m) =>
        {
            if (m.Value == PageNames.Settings)
            {
                ShowSettings();
                return;
            }
            var page = Pages.FirstOrDefault(x => x.Name == m.Value);
            if (page is not null)
                SelectedPage = page;
        });

        Messenger.Register<OpenAiContextRequest>(this, (r, m) =>
        {
            var page = Pages.FirstOrDefault(item => item.Name == PageNames.Scripts);
            if (page?.ViewModel is not ScriptManagementViewModel scriptManagement)
                return;
            scriptManagement.SelectedDetailTabIndex = ScriptManagementViewModel.AiContextTabIndex;
            SelectedPage = page;
        });

        Messenger.Register<ConfigUpdateEvent>(this, (r, m) =>
        {
            var selectedName = SelectedPage?.Name;
            _lifecycle.ReRegister();
            if (_fixedPages.FirstOrDefault(page => page.Name == PageNames.DiaryEditor)?.ViewModel
                is DiaryEditorViewModel diaryEditor)
            {
                diaryEditor.RefreshTrackerTabHeaders();
            }
            _fixedPages = BuildFixedPages();
            Pages.Clear();
            foreach (var page in _fixedPages)
                Pages.Add(page);
            RefreshTrackerPages();
            SelectedPage = Pages.FirstOrDefault(x => x.Name == selectedName) ?? _fixedPages[0];
            _logger.LogDebug("config updated, tracker navigation rebuilt: {Count} pages", Pages.Count);
        });

        Messenger.Register<NotifyEvent>(this, (r, m) =>
        {
            PostUiAsync(async () =>
            {
                var evt = m.Value;
                var vm = _serviceProvider.GetRequiredService<StandardMessageViewModel>();
                vm.Body = evt.Body;
                var options = new OverlayDialogOptions()
                {
                    Title = evt.Title,
                    CanDragMove = false,
                    CanResize = false,
                    CanLightDismiss = evt.LightDismiss,
                    IsCloseButtonVisible = false,
                    Mode = evt.Mode,
                    Buttons = evt.Button,
                };

                if (m.Value.Modal)
                    await OverlayDialog.ShowModal<StandardMessageView, StandardMessageViewModel>(vm, options: options);
                else
                    OverlayDialog.Show<StandardMessageView, StandardMessageViewModel>(vm, options: options);
            }, "通知对话框");
        });

        Messenger.Register<RunCommandEvent>(this, (r, m) => { ExecuteSettingCommand(m.Value); });

        Messenger.Register<ToastEvent>(this, (r, m) => { ToastManager?.Show(m.Value, m.Type); });

        Messenger.Register<ConfirmRequest<ConfirmMessage, bool>>(this, async (r, m) =>
        {
            var result = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var req = m.Request;
                var result = await MessageBox.ShowOverlayAsync(req.Message, req.Title, icon: MessageBoxIcon.Question,
                    button: MessageBoxButton.YesNo);
                return result == MessageBoxResult.Yes;
            });
            m.Reply(result);
        });

        if (App.Instance.DatabaseOk && !App.Instance.AppConfig.ViewSettings.HasCompletedOnboarding)
            ShowOnboarding(automatic: true);

        if (App.Instance.AppConfig.UpdateSettings.AutoCheck)
            PostUiAsync(() => CheckForUpdatesAsync(automatic: true), "自动检查更新");

    }

    private void ShowOnboarding(bool automatic = false)
    {
        PostUiAsync(async () =>
        {
            var viewModel = _serviceProvider.GetRequiredService<OnboardingViewModel>();
            var result = await OverlayDialog.ShowCustomModal<OnboardingAction>(viewModel, options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = false,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
            });
            if (result == OnboardingAction.Later && !viewModel.DoNotShowAgain)
                return;

            if (viewModel.DoNotShowAgain || (automatic && result != OnboardingAction.Later))
            {
                App.Instance.AppConfig.ViewSettings.HasCompletedOnboarding = true;
                EasySaveLoad.Save(App.Instance.AppConfig);
            }
            if (result == OnboardingAction.OpenDatabaseSettings)
                ExecuteSettingCommand(CommandNames.ShowDbSettings);
        }, "首次使用引导");
    }

    private IReadOnlyList<NavigateInfo> BuildFixedPages()
    {
        var built = new List<NavigateInfo>();
        int idx = 1;
        built.Add(new NavigateInfo(PageNames.DiaryEditor, "mdi-notebook",
            _serviceProvider.GetService<DiaryEditorViewModel>(), $"Alt+{idx++}"));
        built.Add(new NavigateInfo(PageNames.WorkItemQuery, "fa-magnifying-glass",
            _serviceProvider.GetRequiredService<WorkItemQueryViewModel>(), $"Alt+{idx++}"));
        built.Add(new NavigateInfo(PageNames.Statistics, "fa-chart-pie",
            _serviceProvider.GetRequiredService<StatisticsViewModel>(), $"Alt+{idx++}"));
        if (App.Instance.AppConfig.SurveySettings.IsServerEnabled)
        {
            built.Add(new NavigateInfo(PageNames.SurveyTool, "mdi-chat-processing-outline",
                _serviceProvider.GetRequiredService<SurveyViewModel>(), $"Alt+{idx++}"));
        }
        if (App.Instance.AppConfig.ViewSettings.ShowDeveloperFeatures)
        {
            built.Add(new NavigateInfo(PageNames.Scripts, "mdi-script-text-outline",
                _serviceProvider.GetRequiredService<ScriptManagementViewModel>(), $"Alt+{idx++}"));
        }
        return built;
    }

    private void RefreshTrackerPages()
    {
        var trackers = _serviceProvider.GetRequiredService<TrackerUiContributionRegistry>().Contributions;
        var dynamicPages = trackers
            .Select(t => (Contribution: t, Page: t.CreateManagementPage(t.Instance.InstanceId)))
            .Where(item => item.Page is not null)
            .Select((item, index) => new NavigateInfo(
                item.Contribution.Instance.DisplayName,
                item.Contribution.Instance.Icon,
                item.Page,
                $"Alt+{_fixedPages.Count + index + 1}"))
            .ToList();

        while (Pages.Count > _fixedPages.Count)
            Pages.RemoveAt(Pages.Count - 1);
        foreach (var page in dynamicPages)
            Pages.Add(page);
    }

    [RelayCommand]
    private void SwitchPage(NavigateInfo info)
    {
        SelectedPage = info;
    }

    public void ExecuteSettingCommand(string cmd)
    {
        switch (cmd)
        {
            case CommandNames.CheckForUpdates:
                PostUiAsync(() => CheckForUpdatesAsync(automatic: false), "检查更新");
                return;
            case CommandNames.ShowOnboarding:
                ShowOnboarding();
                return;
            case CommandNames.ShowDbSettings:
                PostUiAsync(async () =>
                {
                    var options = new OverlayDialogOptions()
                    {
                        CanDragMove = false,
                        CanResize = false,
                        CanLightDismiss = false,
                        Mode = DialogMode.None,
                        IsCloseButtonVisible = false,
                    };
                    var vm = _serviceProvider.GetRequiredService<GenericConfigViewModel>();
                    var oldDriver = App.Instance.AppConfig.DbSettings.DatabaseDriver;
                    _logger.LogDebug("打开数据库配置：目标驱动 {Driver}", oldDriver);
                    var targetFactory = ((App)App.Instance).GetDbFactory(oldDriver);
                    if (targetFactory is null)
                    {
                        EventDispatcher.Notify("错误", $"数据库驱动 {oldDriver} 不可用");
                        return;
                    }

                    var dbConfig = targetFactory.GetConfig();
                    _logger.LogDebug("打开数据库配置对象：驱动 {Driver}，类型 {ConfigType}",
                        targetFactory.Name, dbConfig.GetType().FullName);
                    var oldDbConfig = JObject.FromObject(dbConfig);
                    vm.InitSettings("数据库设置", dbConfig);
                    bool result = await OverlayDialog.ShowCustomModal<bool>(vm, options: options);
                    _logger.LogInformation("db settings updated: {result}", result);
                    if (result)
                    {
                        if (!((App)App.Instance).ReconfigureDatabase(out var error))
                        {
                            App.Instance.AppConfig.DbSettings.DatabaseDriver = oldDriver;
                            JsonConvert.PopulateObject(oldDbConfig.ToString(Formatting.None), dbConfig);
                            EasySaveLoad.Save(dbConfig);
                            EventDispatcher.Notify("错误", error);
                            return;
                        }

                        EventDispatcher.Msg(new ConfigUpdateEvent());
                    }
                }, "数据库设置");
                return;
            case CommandNames.BackupDatabase:
                PostUiAsync(BackupDatabaseAsync, "备份数据库");
                return;
            case CommandNames.RestoreDatabase:
                PostUiAsync(RestoreDatabaseAsync, "还原数据库");
                return;
            case CommandNames.ShowMigrateGuide:
                PostUiAsync(async () =>
                {
                    if (App.Instance.UseDb is null)
                    {
                        EventDispatcher.ShowToast("需要先连接数据库！");
                        return;
                    }

                    var options = new OverlayDialogOptions()
                    {
                        CanDragMove = false,
                        CanResize = false,
                        CanLightDismiss = false,
                        Mode = DialogMode.Warning,
                        IsCloseButtonVisible = false,
                    };
                    var vm = _serviceProvider.GetRequiredService<DbMigrationViewModel>();
                    bool result = await OverlayDialog.ShowCustomModal<bool>(vm, options: options);
                    _logger.LogInformation("migration result is: {result}", result);
                    if (result)
                        EventDispatcher.DbChanged();
                }, "数据库迁移");
                return;
            case CommandNames.EditWorkTags:
                PostUiAsync(async () =>
                {
                    if (App.Instance.UseDb is null)
                    {
                        EventDispatcher.ShowToast("需要先连接数据库！");
                        return;
                    }

                    var options = new OverlayDialogOptions()
                    {
                        CanDragMove = false,
                        CanResize = false,
                        CanLightDismiss = false,
                        Mode = DialogMode.None,
                        IsCloseButtonVisible = false,
                    };
                    var vm = _serviceProvider.GetRequiredService<TagEditorViewModel>();
                    await OverlayDialog.ShowCustomModal<object>(vm, options: options);
                }, "编辑工作标签");
                return;
            case CommandNames.EditWorkTemplates:
                PostUiAsync(async () =>
                {
                    if (App.Instance.UseDb is null)
                    {
                        EventDispatcher.ShowToast("需要先连接数据库！");
                        return;
                    }

                    var options = new OverlayDialogOptions()
                    {
                        CanDragMove = false,
                        CanResize = false,
                        CanLightDismiss = false,
                        Mode = DialogMode.None,
                        IsCloseButtonVisible = false,
                    };
                    var vm = _serviceProvider.GetRequiredService<TemplateEditorViewModel>();
                    await OverlayDialog.ShowCustomModal<object>(vm, options: options);
                }, "编辑工作模板");
                return;
            case CommandNames.RaiseMainWindow:
                Dispatcher.UIThread.Post(() =>
                {
                    if (!Window!.IsVisible)
                    {
                        Window.Show();
                    }
                    else
                    {
                        Window.Activate();
                    }
                });
                return;
            case CommandNames.QuitApp:
                Dispatcher.UIThread.Post(Quit);
                return;
            case CommandNames.ShowAboutDialog:
                Dispatcher.UIThread.Post(ShowAbout);
                return;
        }

        throw new ArgumentOutOfRangeException(nameof(cmd));
    }

    private async Task CheckForUpdatesAsync(bool automatic)
    {
        if (automatic)
            await Task.Delay(TimeSpan.FromSeconds(15));
        else
            EventDispatcher.ShowToast("正在检查更新…");

        var result = await _serviceProvider.GetRequiredService<AppUpdateService>()
            .CheckAsync(App.Instance.AppConfig.UpdateSettings);
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable:
                {
                    var manifest = result.Envelope!.Manifest;
                    var package = result.Envelope.FullPackage;
                    var confirmed = await EventDispatcher.Confirm(
                        "发现新版本",
                        $"当前版本：{AppInfo.AppVersionString}\n"
                        + $"最新版本：{manifest.VersionId}（序号 {manifest.Sequence}）\n"
                        + $"完整包：{FormatSize(package.Size)}\n\n"
                        + "是否立即下载并安装？准备完成后应用会自动退出并重启。");
                    if (!confirmed)
                        return;

                    EventDispatcher.ShowToast("正在下载并校验更新完整包，请勿退出应用…");
                    try
                    {
                        var updateService = _serviceProvider.GetRequiredService<AppUpdateService>();
                        var prepared = await updateService.PrepareAsync(result);
                        if (prepared.PreservedConflicts.Count > 0)
                        {
                            EventDispatcher.Notify(
                                "已保留本地修改文件",
                                "以下旧版本文件有本地修改，更新不会删除它们：\n"
                                + string.Join('\n', prepared.PreservedConflicts));
                        }
                        EventDispatcher.ShowToast("更新准备完成，应用即将重启…", NotificationType.Success);
                        updateService.StartPreparedUpdate(prepared);
                        Quit();
                    }
                    catch (OperationCanceledException)
                    {
                        EventDispatcher.ShowToast("更新已取消", NotificationType.Warning);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "下载或准备应用更新失败");
                        EventDispatcher.Notify("更新准备失败", exception.Message);
                    }
                    return;
                }
            case UpdateCheckStatus.UpToDate:
                if (!automatic)
                    EventDispatcher.ShowToast("当前已是最新版本", NotificationType.Success);
                return;
            case UpdateCheckStatus.NoPublishedVersion:
                if (!automatic)
                    EventDispatcher.ShowToast("更新服务器没有当前平台和包类型的发布快照", NotificationType.Warning);
                return;
            case UpdateCheckStatus.UnsupportedUpdater:
                EventDispatcher.Notify("暂时无法更新", result.Error ?? "当前更新器协议版本过低。");
                return;
            case UpdateCheckStatus.TemporarilyUnavailable:
                if (!automatic)
                    EventDispatcher.ShowToast(result.Error ?? "更新服务器暂时不可用", NotificationType.Warning);
                return;
            case UpdateCheckStatus.InvalidResponse:
                if (!automatic)
                    EventDispatcher.Notify("检查更新失败", result.Error ?? "更新服务器响应无效。");
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static string FormatSize(long bytes)
    {
        const double megabyte = 1024 * 1024;
        return bytes >= megabyte
            ? $"{bytes / megabyte:F1} MiB"
            : $"{bytes / 1024d:F1} KiB";
    }

    private async Task BackupDatabaseAsync()
    {
        if (App.Instance.UseDb is not IDbMaintenanceProvider provider)
        {
            EventDispatcher.Notify("不支持备份", $"数据库驱动 {App.Instance.UseFactory?.Name ?? "<unknown>"} 当前不支持应用内备份。");
            return;
        }

        var support = provider.GetMaintenanceSupport();
        if (!support.CanBackup)
        {
            EventDispatcher.Notify("不支持备份", support.UnavailableReason ?? "当前数据库不支持应用内备份。");
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(View)?.StorageProvider;
        if (storageProvider is null)
        {
            EventDispatcher.Notify("备份失败", "无法打开文件选择器。");
            return;
        }

        var isPostgreSql = string.Equals(App.Instance.UseDb?.ProviderName, "PgDb", StringComparison.Ordinal);
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "备份数据库",
            SuggestedFileName = $"DiaryApp-backup-{DateTime.Now:yyyyMMdd-HHmmss}.{(isPostgreSql ? "dump" : "sqlite3")}",
            DefaultExtension = isPostgreSql ? "dump" : "sqlite3",
            FileTypeChoices =
            [
                new FilePickerFileType("SQLite 数据库备份") { Patterns = ["*.sqlite3", "*.bak"] },
                new FilePickerFileType("PostgreSQL 数据库备份") { Patterns = ["*.dump", "*.backup", "*.bak"] },
            ],
        });
        if (file is null)
            return;

        var result = await Task.Run(() => provider.CreateBackup(file.Path.LocalPath));
        if (!result.Success)
        {
            EventDispatcher.Notify("备份失败", result.Error ?? "数据库备份创建失败。");
            return;
        }

        EventDispatcher.Notify("备份完成", $"数据库备份已保存到：\n{result.BackupPath}");
    }

    private async Task RestoreDatabaseAsync()
    {
        if (App.Instance.UseDb is not IDbMaintenanceProvider provider)
        {
            EventDispatcher.Notify("不支持还原", $"数据库驱动 {App.Instance.UseFactory?.Name ?? "<unknown>"} 当前不支持应用内还原。");
            return;
        }

        var support = provider.GetMaintenanceSupport();
        if (!support.CanRestore)
        {
            EventDispatcher.Notify("不支持还原", support.UnavailableReason ?? "当前数据库不支持应用内还原。");
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(View)?.StorageProvider;
        if (storageProvider is null)
        {
            EventDispatcher.Notify("还原失败", "无法打开文件选择器。");
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择数据库备份",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SQLite 数据库备份") { Patterns = ["*.sqlite3", "*.bak"] },
                new FilePickerFileType("PostgreSQL 数据库备份") { Patterns = ["*.dump", "*.backup", "*.bak"] },
            ],
        });
        var file = files.FirstOrDefault();
        if (file is null)
            return;

        var validation = await Task.Run(() => provider.ValidateBackup(
            file.Path.LocalPath,
            DataVersion.VersionCode));
        if (!validation.Success)
        {
            EventDispatcher.Notify("备份无效", validation.Error ?? "所选文件不是可还原的数据库备份。");
            return;
        }

        var restoreSafetyMessage = string.Equals(validation.ProviderName, "PostgreSQL", StringComparison.Ordinal)
            ? "PostgreSQL 还原只允许写入不存在或不包含 DiaryApp 已知表的目标数据库，不会覆盖当前数据库。"
            : "当前数据库会先生成安全副本，还原将在下次启动时执行。";
        var confirmed = await MessageBox.ShowOverlayAsync(
            $"将还原 {validation.ProviderName} 数据库" +
            (validation.DataVersion == 0
                ? "。备份数据版本将在还原后的启动检查中确认。"
                : $"，数据版本 0x{validation.DataVersion:X8}。") + "\n\n" +
            restoreSafetyMessage + "\n是否继续？",
            "确认还原数据库",
            icon: MessageBoxIcon.Warning,
            button: MessageBoxButton.YesNo);
        if (confirmed != MessageBoxResult.Yes)
            return;

        var stage = _serviceProvider.GetRequiredService<DatabaseRestoreCoordinator>()
            .Stage(validation.ProviderName, file.Path.LocalPath);
        if (!stage.Success)
        {
            EventDispatcher.Notify("还原暂存失败", stage.Error ?? "无法暂存数据库还原任务。");
            return;
        }

        EventDispatcher.Notify(
            "还原已安排",
            "备份已通过校验。请退出并重新启动 DiaryApp；下次启动会执行还原并在失败时自动恢复当前数据库。");
    }

    private void PostUiAsync(Func<Task> action, string operation)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UI operation failed: {Operation}", operation);
            }
        });
    }

    private bool _quiting;
    private Window? Window => View as Window;

    [RelayCommand]
    private void Restart()
    {
        Program.RequestRestart();
        Quit();
    }

    [RelayCommand]
    private void Quit()
    {
        _quiting = true;
        (View as Window)?.Close();
    }

    [RelayCommand(CanExecute = nameof(CanMinimized))]
    private void Minimized()
    {
        Window!.WindowState = WindowState.Minimized;
    }

    private bool CanMinimized()
    {
        return Window?.WindowState != WindowState.Minimized;
    }

    [RelayCommand(CanExecute = nameof(CanMaximized))]
    private void Maximized()
    {
        Window!.WindowState = WindowState.Maximized;
    }

    private bool CanMaximized()
    {
        return Window?.WindowState != WindowState.Maximized;
    }

    protected override void OnAttachView(Control? view)
    {
        Window? window = view as Window;
        window!.PropertyChanged += (sender, args) =>
        {
            if (args.Property == Window.WindowStateProperty)
            {
                MinimizedCommand.NotifyCanExecuteChanged();
                MaximizedCommand.NotifyCanExecuteChanged();
            }
        };
    }

    [RelayCommand]
    private void Closing(object? parameter)
    {
        if (_quiting)
            return;
        if (!AllConfig.Instance.ViewSettings.HideToTray)
            return;
        if (parameter is WindowClosingEventArgs args)
        {
            args.Cancel = true;
            Window!.Hide();
            Messenger.Send(new WindowStateEvent(false));
        }
    }

    [RelayCommand]
    private void Opened(object? parameter)
    {
        Messenger.Send(new WindowStateEvent(true));
    }

    [RelayCommand]
    private void OpenUserManual()
    {
        try
        {
            _userManualService.Open();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException or PlatformNotSupportedException)
        {
            _logger.LogError(exception, "打开用户手册失败");
            EventDispatcher.Notify("无法打开用户手册", exception.Message);
        }
    }

    [RelayCommand]
    private void ShowAbout()
    {
        if (!Window!.IsVisible)
            Window.Show();
        var options = new OverlayDialogOptions()
        {
            Title = "关于此软件",
            Mode = DialogMode.Info,
            Buttons = DialogButton.None,
            CanDragMove = false,
            CanLightDismiss = true,
            IsCloseButtonVisible = true,
        };
        OverlayDialog.Show(_serviceProvider.GetRequiredService<AboutViewModel>(), null, options);
    }

    [RelayCommand]
    private void ShowTemplateSettings()
        => ExecuteSettingCommand(CommandNames.EditWorkTemplates);

    [RelayCommand]
    private void ImportScriptExtension()
        => PostUiAsync(ImportScriptExtensionAsync, "导入脚本扩展");

    private async Task ImportScriptExtensionAsync()
    {
        var storageProvider = Window?.StorageProvider;
        if (storageProvider is null)
        {
            EventDispatcher.Notify("导入失败", "当前没有可用的文件选择器。");
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入脚本扩展",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("DiaryApp 脚本共享包")
                {
                    Patterns = [$"*{ScriptSharePackageService.FileExtension}"],
                },
            ],
        });
        var file = files.FirstOrDefault();
        if (file is null)
            return;

        var scriptRoot = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");
        try
        {
            var loadState = _serviceProvider.GetRequiredService<ScriptDirectoryLoadState>();
            var current = await loadState.EnsureLoadedAsync(scriptRoot);
            var existing = current.Entries.Select(ToExistingScript).ToArray();
            var sharePackageService = _serviceProvider.GetRequiredService<ScriptSharePackageService>();
            var preview = await sharePackageService.InspectAsync(file.Path.LocalPath, scriptRoot, existing);
            var dialog = _serviceProvider.GetRequiredService<ScriptShareImportDialogViewModel>();
            dialog.Initialize(preview);
            var selection = await OverlayDialog.ShowCustomModal<ScriptShareImportSelection>(
                dialog,
                options: new OverlayDialogOptions
                {
                    CanDragMove = false,
                    CanResize = false,
                    CanLightDismiss = false,
                    IsCloseButtonVisible = false,
                });
            if (selection is null || selection.Decisions.Count == 0)
                return;

            var result = await sharePackageService.ImportAsync(
                preview,
                scriptRoot,
                selection.Decisions,
                existing);
            var reloaded = await loadState.ReloadAsync(scriptRoot);
            _serviceProvider.GetRequiredService<ScriptAutomationScheduler>().ApplyLoadResult(reloaded);

            var importedIds = selection.Decisions
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (CurrentPageModel is ScriptManagementViewModel scriptManagement)
                await scriptManagement.RefreshAfterImportAsync(importedIds);

            EventDispatcher.Notify(
                "脚本扩展导入完成",
                $"已导入 {result.ImportedCount} 个脚本，跳过 {result.SkippedCount} 个。扩展已重新加载；如需查看源码或诊断，请开启开发者功能。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogError(exception, "导入脚本扩展失败：{PackagePath}", file.Path.LocalPath);
            EventDispatcher.Notify("脚本扩展导入失败", exception.Message);
        }
    }

    private static ScriptShareExistingItem ToExistingScript(ScriptDirectoryEntry entry) => new(
        entry.BuildResult?.Program?.Descriptor.Id
            ?? entry.Metadata?.Id
            ?? Path.GetFileNameWithoutExtension(entry.SourcePath),
        entry.SourcePath);

    [RelayCommand]
    private void ShowTagSettings()
        => ExecuteSettingCommand(CommandNames.EditWorkTags);

    [RelayCommand]
    private void ShowSettings()
    {
        PostUiAsync(async () =>
        {
            var viewModel = _serviceProvider.GetRequiredService<SettingsViewModel>();
            await OverlayDialog.ShowCustomModal<object>(viewModel, options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = true,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
            });
        }, "设置对话框");
    }

    [RelayCommand]
    private void ShowExportTemplateSettings()
    {
        PostUiAsync(async () =>
        {
            var viewModel = _serviceProvider.GetRequiredService<ExportTemplateManagerViewModel>();
            await OverlayDialog.ShowCustomModal<object>(viewModel, options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = true,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
            });
        }, "数据模板管理对话框");
    }

    [RelayCommand]
    private void ShowTrackerSettings()
    {
        PostUiAsync(async () =>
        {
            var viewModel = _serviceProvider.GetRequiredService<TrackerSettingsDialogViewModel>();
            await OverlayDialog.ShowCustomModal<object>(viewModel, options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = true,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
            });
        }, "Tracker 配置对话框");
    }
}
