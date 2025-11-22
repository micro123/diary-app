using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Ursa.Controls;

namespace Diary.App.ViewModels;

public class ViewModelBase : ObservableObject
{
    public Control? View { get; private set; }
    private WindowNotificationManager? _notificationManager;
    private WindowToastManager? _toastManager;
    private TopLevel? _topLevel;

    protected WindowNotificationManager? NotificationManager =>
        _notificationManager ??= WindowNotificationManager.TryGetNotificationManager(View, out var manager)
            ? manager
            : new WindowNotificationManager(TopLevel);

    protected WindowToastManager? ToastManager =>
        _toastManager ??= WindowToastManager.TryGetToastManager(View, out var manager)
            ? manager
            : new WindowToastManager(TopLevel);

    private TopLevel? TopLevel =>
        _topLevel ??= TopLevel.GetTopLevel(View);

    protected async Task<bool> CopyStringToClipboardAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (TopLevel?.Clipboard == null)
        {
            return false;
        }

        await TopLevel.Clipboard.SetTextAsync(text);
        return true;
    }
    
    protected WeakReferenceMessenger Messenger => WeakReferenceMessenger.Default;
    
    protected virtual void OnAttachView(Control? view) { }

    public void SetView(Control? view)
    {
        View = view;
        OnAttachView(View);
    }
}