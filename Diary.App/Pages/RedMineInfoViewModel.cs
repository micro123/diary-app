using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.ViewModels;
using Diary.RedMine;
using Diary.RedMine.Response;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Pages;

[DiAutoRegister]
public partial class RedMineInfoViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty] private bool _serverOk = false;
    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _userLogin = string.Empty;

    public RedMineInfoViewModel(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        Messenger.Register<ConfigUpdateEvent>(this, (r, m) => { CheckServer(); });

        Task.Run(CheckServer);
    }

    private void CheckServer()
    {
        var ok = RedMineApis.GetUserInfo(out var info);
        Dispatcher.UIThread.Post(() => { UpdateUserInfo(info); });
    }

    private void UpdateUserInfo(UserInfo? userInfo)
    {
        if (userInfo is not null)
        {
            ServerOk = true;
            UserName = $"{userInfo.LastName}{userInfo.FirstName}";
            UserId = userInfo.Id;
            UserLogin = userInfo.Login;
        }
        else
        {
            ServerOk = false;
            UserName = string.Empty;
            UserId = 0;
            UserLogin = string.Empty;
        }
    }
}