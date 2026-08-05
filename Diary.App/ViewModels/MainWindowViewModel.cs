using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.Core.Constants;
using Diary.App.Models;
using Diary.Core.Data.AppConfig;
using Diary.Core.Utils;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ursa.Controls;
using AboutViewModel = Diary.App.ViewModels.Dialogs.AboutViewModel;
using DbMigrationViewModel = Diary.App.ViewModels.Dialogs.DbMigrationViewModel;
using GenericConfigViewModel = Diary.GUIBase.ViewModels.Dialogs.GenericConfigViewModel;
using StandardMessageView = Diary.App.Views.Dialogs.StandardMessageView;
using StandardMessageViewModel = Diary.App.ViewModels.Dialogs.StandardMessageViewModel;
using TagEditorViewModel = Diary.App.ViewModels.Dialogs.TagEditorViewModel;
using TemplateEditorViewModel = Diary.App.ViewModels.Dialogs.TemplateEditorViewModel;

namespace Diary.App.ViewModels;

[DiAutoRegister(singleton: true)]
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly StatusBarViewModel _statusBarViewModel;
    private readonly IServiceProvider _serviceProvider;
    private readonly TrackerPluginLifecycleCoordinator _lifecycle;
    private readonly ILogger _logger;
    public string VersionString => AppInfo.AppVersionString;

    public string VersionDetails => AppInfo.AppVersionDetails;
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

        // 导航可扩展：[日记] + tracker 贡献页 + [统计/调查/设置]。
        // 手势按最终位置分配 Alt+1..；单 tracker（RedMine）下顺序/手势与原硬编码一致。
        BuildPages();
        _statusBarViewModel = _serviceProvider.GetRequiredService<StatusBarViewModel>();

        SelectedPage = Pages[0];

        Messenger.Register<PageSwitchEvent>(this, (r, m) =>
        {
            var page = Pages.FirstOrDefault(x => x.Name == m.Value);
            if (page is not null)
                SelectedPage = page;
        });

        Messenger.Register<ConfigUpdateEvent>(this, (r, m) =>
        {
            var selectedName = SelectedPage?.Name;
            _lifecycle.ReRegister();
            BuildPages();
            SelectedPage = Pages.FirstOrDefault(x => x.Name == selectedName) ?? Pages[0];
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

        Messenger.Register<ToastEvent>(this, (r, m) => { ToastManager?.Show(m.Value); });

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

    }

    private void BuildPages()
    {
        var built = new List<NavigateInfo>();
        var trackers = _serviceProvider.GetRequiredService<TrackerUiContributionRegistry>().Contributions;
        int idx = 1;
        built.Add(new NavigateInfo(PageNames.DiaryEditor, "mdi-notebook",
            _serviceProvider.GetService<DiaryEditorViewModel>(), $"Alt+{idx++}"));
        built.Add(new NavigateInfo(PageNames.WorkItemQuery, "fa-magnifying-glass",
            _serviceProvider.GetRequiredService<WorkItemQueryViewModel>(), $"Alt+{idx++}"));
        foreach (var t in trackers)
        {
            var page = t.CreateManagementPage(t.Instance.InstanceId);
            if (page is null)
                continue;
            built.Add(new NavigateInfo(t.Instance.DisplayName, t.Instance.Icon, page, $"Alt+{idx++}"));
        }
        built.Add(new NavigateInfo(PageNames.Statistics, "fa-chart-pie",
            _serviceProvider.GetRequiredService<StatisticsViewModel>(), $"Alt+{idx++}"));
        built.Add(new NavigateInfo(PageNames.SurveyTool, "mdi-chat-processing-outline",
            _serviceProvider.GetRequiredService<SurveyViewModel>(), $"Alt+{idx++}"));
        built.Add(new NavigateInfo(PageNames.Settings, "mdi-cog-outline",
            _serviceProvider.GetService<SettingsViewModel>(), $"Alt+{idx++}"));
        Pages = new ObservableCollection<NavigateInfo>(built);
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
    private void ShowSettings()
    {
        EventDispatcher.RouteToPage(PageNames.Settings);
    }
}
