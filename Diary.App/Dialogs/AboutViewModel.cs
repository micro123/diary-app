using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.Dialogs;

[DiAutoRegister]
public partial class AboutViewModel: ViewModelBase, IDialogContext
{
    public void Close()
    {
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<object?>? RequestClose;

    public string AppVersionString => AppInfo.AppVersionString;

    public string InfoText => """
                              Diary Tool NG - A rewrite of Diary App using C#.
                              Libraries (not complete):
                                  Avalonia, DataGrid, TreeDataGrid
                                  CommunityToolkit.Mvvm
                                  Semi.Avalonia, Irihi.Ursa
                                  LiveCharts2
                                  Projektanker.Icons.Avalonia
                                  Xaml.Behaviors.Avalonia
                                  Newtonsoft.Json
                                  SourceGear.sqlite3, Npgsql
                                  Microsoft.Extensions.Logging, Serilog
                                  RestSharp, RestSharp.Serializers.NewtonsoftJson
                                  NanomsgNG.NET
                              Source code: https://github.com/micro123/diary-app
                              License: GPL-3.0
                              """;

    public string AppVersionDetails => AppInfo.AppVersionDetails;

    [RelayCommand]
    private async Task CopyVersion()
    {
        if (await CopyStringToClipboardAsync(AppVersionString))
        {
            ToastManager?.Show("已复制");
        }
    }
}