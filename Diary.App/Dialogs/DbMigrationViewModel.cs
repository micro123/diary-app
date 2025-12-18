using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Diary.MigrationTool;

namespace Diary.App.Dialogs;

[DiAutoRegister]
public partial class DbMigrationViewModel: ViewModelBase, IDialogContext
{
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

    public DbMigrationViewModel()
    {
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

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task DoMigrate()
    {
        Working = true;
        await Task.Run(() =>
        {
            var db = App.Current.UseDb;
            if (db == null)
                return; // error: must connect to db first!
            if (SqliteMode)
            {
                if (!File.Exists(SqlitePath))
                {
                    // error: file not exists!
                    return;
                }

                Migrator.MigrateFromSqlite(db, SqlitePath);
            }
            else
            {
                // check params
                if (!string.IsNullOrEmpty(Host) &&
                    !string.IsNullOrEmpty(Database) &&
                    !string.IsNullOrEmpty(User) &&
                    !string.IsNullOrEmpty(Password))
                {
                    Migrator.MigrateFromPgsql(db, Host, Port, Database, User, Password);
                }
                else
                {
                    // error: param(s) missing!
                }
            }
        });
        Working = false;
    }

    private bool CanWork => !Working;

    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;
}