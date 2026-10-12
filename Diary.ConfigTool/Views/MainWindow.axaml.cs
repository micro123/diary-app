using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Diary.ConfigTool.ViewModels;

namespace Diary.ConfigTool.Views;

public sealed partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

    private async void ChooseDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 Diary.App 配置目录",
            AllowMultiple = false,
        });
        var path = result.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            Run(() => ViewModel.ChangeDirectory(path));
    }

    private void ApplyDirectoryClick(object? sender, RoutedEventArgs e)
        => Run(() => ViewModel.ChangeDirectory(ViewModel.ConfigDirectory));

    private void ReloadDirectoryClick(object? sender, RoutedEventArgs e)
        => Run(ViewModel.ReloadDirectory);

    private void ValidateJsonClick(object? sender, RoutedEventArgs e)
        => ViewModel.FormatAndValidateJson();

    private void ApplyPropertiesClick(object? sender, RoutedEventArgs e)
        => ViewModel.ApplyPropertyRows();

    private async void SaveClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "保存配置",
                "保存会修改当前配置文件，并将原文件写入 .bak。请确认主程序已经退出。"))
        {
            return;
        }
        Run(ViewModel.SaveSelected);
    }

    private async void RestoreBackupClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "恢复配置备份",
                "将使用 .bak 替换当前配置。恢复前的文件会另存为带时间戳的副本。"))
        {
            return;
        }
        Run(ViewModel.RestoreSelectedBackup);
    }

    private async void ImportCurrentClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入明文 JSON",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] }],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            Run(() => ViewModel.ImportCurrent(path));
    }

    private async void ExportCurrentClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "导出明文配置",
                "导出的 JSON 不加密，可能包含密码、令牌或 API Key。请妥善保管。"))
        {
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出明文 JSON",
            SuggestedFileName = ViewModel.SelectedConfiguration?.Id + ".json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is not null)
            Run(() => ViewModel.ExportCurrent(path));
    }

    private async void ExportAllClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "导出全部明文配置",
                "导出包不会加密，其中可能包含数据库密码、Tracker 凭据、模型 API Key 和代理凭据。"))
        {
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出位置",
            AllowMultiple = false,
        });
        var parent = folders.FirstOrDefault()?.TryGetLocalPath();
        if (parent is null)
            return;
        var output = Path.Combine(parent, "Diary.Config.Export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Run(() => ViewModel.ExportAll(output));
    }

    private async void ImportAllClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含 manifest.json 的明文配置包",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
            return;
        if (!await ConfirmAsync(
                "导入配置包",
                "导入会修改多项配置。工具会先创建完整配置快照，但仍请确认主程序已经退出。"))
        {
            return;
        }
        Run(() => ViewModel.ImportAll(path));
    }

    private async void ResetEncryptionIdentityClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "重新初始化加密身份",
                "仅当切换 Windows 域或用户后，旧主密钥已无法解密时使用。工具会先创建完整快照，保留旧主密钥，再允许当前身份导入明文配置。未重新导入的加密配置仍不可读取。"))
        {
            return;
        }
        Run(() => ViewModel.ResetEncryptionIdentity());
    }

    private async void MigrateWindowsDpapiClick(object? sender, RoutedEventArgs e)
    {
        var input = await ShowDpapiMigrationDialogAsync();
        if (!input.Accepted)
            return;
        try
        {
            ViewModel.MigrateWindowsDpapi(input.OldSid, input.OldPassword);
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = $"DPAPI 迁移失败：{exception.GetBaseException().Message}";
        }
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = $"操作失败：{exception.GetBaseException().Message}";
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var confirm = new Button { Content = "确认", MinWidth = 88 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        confirm.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm },
                },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<(bool Accepted, string OldSid, string OldPassword)> ShowDpapiMigrationDialogAsync()
    {
        var accepted = false;
        var candidates = ViewModel.PreviousSidCandidates;
        var sidSelector = new ComboBox
        {
            ItemsSource = candidates,
            SelectedIndex = candidates.Count > 0 ? 0 : -1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var manualSidBox = new TextBox
        {
            Watermark = "S-1-5-21-...",
            IsVisible = candidates.Count == 0,
        };
        var manualSidCheck = new CheckBox
        {
            Content = "手动输入 SID",
            IsChecked = candidates.Count == 0,
        };
        void UpdateSidInputMode()
        {
            var manual = manualSidCheck.IsChecked == true;
            sidSelector.IsEnabled = !manual;
            manualSidBox.IsVisible = manual;
        }
        manualSidCheck.IsCheckedChanged += (_, _) => UpdateSidInputMode();
        UpdateSidInputMode();
        var passwordBox = new TextBox
        {
            PasswordChar = '●',
            RevealPassword = false,
            Watermark = "切换域之前使用的登录密码",
        };
        var dialog = new Window
        {
            Title = "迁移 Windows DPAPI 身份",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var migrate = new Button { Content = "开始迁移", MinWidth = 96 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        migrate.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "此操作会迁移旧 SID 目录中的全部 DPAPI 用户主密钥。成功迁移的旧密钥会被 Windows 删除；工具会在调用前备份新旧 SID 目录和 Diary 配置。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock { Text = "旧 SID", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                sidSelector,
                manualSidCheck,
                manualSidBox,
                new TextBlock
                {
                    Text = candidates.Count == 0
                        ? "没有扫描到其他 SID 目录，请确认旧用户配置仍在当前 profile 中，或手动输入。"
                        : $"已扫描到 {candidates.Count} 个候选目录，并已排除当前用户 SID。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Opacity = 0.72,
                },
                new TextBlock { Text = "旧登录密码", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                passwordBox,
                new TextBlock
                {
                    Text = "密码只用于本次 Windows API 调用，不会保存或写入日志。迁移必须在新的 Windows 用户账号下执行。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Opacity = 0.72,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { cancel, migrate },
                },
            },
        };
        await dialog.ShowDialog(this);
        var oldSid = manualSidCheck.IsChecked == true
            ? manualSidBox.Text?.Trim() ?? string.Empty
            : sidSelector.SelectedIndex >= 0 && sidSelector.SelectedIndex < candidates.Count
                ? candidates[sidSelector.SelectedIndex].Sid
                : string.Empty;
        var result = (accepted, oldSid, passwordBox.Text ?? string.Empty);
        passwordBox.Text = string.Empty;
        return result;
    }
}
