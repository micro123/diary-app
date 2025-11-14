using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Ursa.Controls;

namespace Diary.App.ViewModels;

public class ViewModelBase : ObservableObject
{
    public Control? View { get; set; } = null;
    private WindowNotificationManager? _notificationManager = null;
    private TopLevel? _topLevel = null;

    protected WindowNotificationManager? NotificationManager =>
        _notificationManager ?? (WindowNotificationManager.TryGetNotificationManager(View, out _)
            ? _notificationManager
            : _notificationManager = new WindowNotificationManager(TopLevel.GetTopLevel(View)));

    protected TopLevel? TopLevel =>
        _topLevel ??= TopLevel.GetTopLevel(View);
    
    protected async Task<bool> CopyStringToClipboardAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (TopLevel == null)
        {
            return false;
        }
        await TopLevel.Clipboard!.SetTextAsync(text);
        return true;
    }
}