using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.MigrationTool;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Ursa.Controls;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class DbMigrationViewModel : ViewModelBase, IDialogContext
{
    private readonly ILogger _logger;
    public static IList<string> DbProviders { get; } = ["SQLite", "PostgreSQL"];

    [ObservableProperty] private string _dbType = string.Empty;
    [ObservableProperty] private bool _sqliteMode = true;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    private bool _working;

    // sqlite params
    [ObservableProperty] private string _sqlitePath = string.Empty;

    // postgresql params
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _database = string.Empty;
    [ObservableProperty] private ushort _port = 5432;
    [ObservableProperty] private string _user = string.Empty;
    [ObservableProperty] private string _password = string.Empty;

    // status message
    [ObservableProperty] private bool _status = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _message = string.Empty;

    public DbMigrationViewModel(ILogger logger)
    {
        _logger = logger;
        DbType = DbProviders[0];
    }

    partial void OnDbTypeChanged(string value)
    {
        SqliteMode = value == DbProviders[0];
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void Abort()
    {
        Close();
    }

    private void ProcessCallback(bool success, double progress, string message)
    {
        _logger.LogInformation("Migrating: status {status}, progress {progress}, message {message}", success, progress, message);
        // 回调来自线程池线程，用 Post 异步切回 UI 线程更新绑定，避免同步阻塞工作线程
        Dispatcher.UIThread.Post(() =>
        {
            Status = success;
            Progress = progress * 100.0;
            Message = message;
        });
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task DoMigrate()
    {
        var ans = await MessageBox.ShowOverlayAsync("当前数据将全部丢失，要继续吗？", "操作确认",
            icon: MessageBoxIcon.Warning,
            button: MessageBoxButton.YesNo);
        if (ans != MessageBoxResult.Yes)
            return;

        Working = true;
        var result = await Task.Run(() =>
        {
            var db = App.Instance.UseDb;
            if (db == null)
                return false; // error: must connect to db first!
            if (SqliteMode)
            {
                if (!File.Exists(SqlitePath))
                {
                    // error: file not exists!
                    return false;
                }

                return Migrator.MigrateFromSqlite(db, SqlitePath, ProcessCallback);
            }

            // check params
            if (!string.IsNullOrEmpty(Host) &&
                !string.IsNullOrEmpty(Database) &&
                !string.IsNullOrEmpty(User) &&
                !string.IsNullOrEmpty(Password))
            {
                return Migrator.MigrateFromPgsql(db, Host, Port, Database, User, Password, ProcessCallback);
            }

            // error: param(s) missing!
            return false;
        });
        Working = false;
        if (result)
        {
            EventDispatcher.Notify("成功", "数据迁移完成！");
            RequestClose?.Invoke(this, true);
        }
        else
        {
            EventDispatcher.Notify("错误", $"迁移失败了。。。");
        }
    }

    private bool CanWork => !Working;

    public void Close()
    {
        RequestClose?.Invoke(this, false);
    }

    public event EventHandler<object?>? RequestClose;
}
