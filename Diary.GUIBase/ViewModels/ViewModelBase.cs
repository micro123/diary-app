using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Ursa.Controls;

namespace Diary.GUIBase.ViewModels;

public class ViewModelBase : ObservableObject, IDisposable
{
    public Control? View { get; private set; }
    private WindowNotificationManager? _notificationManager;
    private WindowToastManager? _toastManager;
    private TopLevel? _topLevel;
    private bool _disposed;

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

    protected async Task<string?> SaveTextFileAsync(
        string title,
        string suggestedFileName,
        string extension,
        string content)
    {
        var storageProvider = TopLevel?.StorageProvider;
        if (storageProvider is null)
            return null;

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(extension.ToUpperInvariant())
                {
                    Patterns = [$"*.{extension}"],
                },
            ],
        });
        if (file is null)
            return null;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(content);
        return file.Path.LocalPath;
    }

    protected WeakReferenceMessenger Messenger => WeakReferenceMessenger.Default;

    protected virtual void OnAttachView(Control? view) { }

    public void SetView(Control? view)
    {
        View = view;
        OnAttachView(View);
    }

    public virtual void OnHide() { }
    public virtual void OnShow() { }

    public virtual void Cleanup()
    {
        if (!_disposed)
        {
            _disposed = true;
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
