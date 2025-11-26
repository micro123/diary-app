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
    public string AppVersionDetails => AppInfo.AppVersionDetails;

    public string InfoText => """
                              这里写一些内容。。。
                              TODO: 描述信息
                              TODO: 开源信息
                              """;

    [RelayCommand]
    private async Task CopyVersion()
    {
        if (await CopyStringToClipboardAsync(AppVersionString))
        {
            ToastManager?.Show("已复制");
        }
    }
}