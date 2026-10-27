using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Diary.Agent.UI.ViewModels;

namespace Diary.Agent.UI.Views;

public partial class AiAgentPageView : UserControl
{
    private const double FollowLatestThreshold = 32;
    private readonly HashSet<AiChatMessageViewModel> _trackedMessages = [];
    private AiAgentPageViewModel? _messageSource;
    private bool _isAttached;
    private bool _followLatest = true;
    private bool _scrollQueued;
    private bool _autoScrolling;

    public AiAgentPageView()
    {
        InitializeComponent();
        AiAgentInputTextBox.AddHandler(
            KeyDownEvent,
            OnInputKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DataContextChanged += (_, _) => AttachMessageSource(
            _isAttached ? DataContext as AiAgentPageViewModel : null);
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            _followLatest = true;
            AttachMessageSource(DataContext as AiAgentPageViewModel);
            QueueScrollToLatest(force: true);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            AttachMessageSource(null);
        };
        ChatScrollViewer.ScrollChanged += OnChatScrollChanged;
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not AiAgentPageViewModel viewModel)
            return;
        if (sender is TextBox pasteTextBox
            && e.Key == Key.V
            && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await PasteClipboardAsync(viewModel, pasteTextBox);
            return;
        }
        if (sender is TextBox textBox
            && ShouldNavigateHistory(
                e.Key,
                e.KeyModifiers,
                textBox.Text ?? string.Empty,
                textBox.CaretIndex,
                textBox.SelectionStart,
                textBox.SelectionEnd)
            && viewModel.TryNavigateInputHistory(e.Key == Key.Up, textBox.Text ?? string.Empty))
        {
            e.Handled = true;
            Dispatcher.UIThread.Post(() => MoveCaretToEnd(textBox));
            return;
        }
        if (!ShouldSend(e.Key, e.KeyModifiers)
            || !viewModel.SendCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        viewModel.SendCommand.Execute(null);
    }

    private async Task PasteClipboardAsync(AiAgentPageViewModel viewModel, TextBox textBox)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        try
        {
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                using (bitmap)
                    await viewModel.AddClipboardBitmapAsync(bitmap);
                return;
            }
            var files = await clipboard.TryGetFilesAsync();
            if (await viewModel.AddStorageFilesAsync(files))
                return;
            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
                InsertText(textBox, text);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            viewModel.ReportImagePasteFailure(exception.Message);
        }
    }

    private static void InsertText(TextBox textBox, string value)
    {
        var existing = textBox.Text ?? string.Empty;
        var start = Math.Min(textBox.SelectionStart, textBox.SelectionEnd);
        var end = Math.Max(textBox.SelectionStart, textBox.SelectionEnd);
        start = Math.Clamp(start, 0, existing.Length);
        end = Math.Clamp(end, start, existing.Length);
        textBox.Text = existing[..start] + value + existing[end..];
        var caret = start + value.Length;
        textBox.CaretIndex = caret;
        textBox.SelectionStart = caret;
        textBox.SelectionEnd = caret;
    }

    internal static bool ShouldNavigateHistory(
        Key key,
        KeyModifiers modifiers,
        string text,
        int caretIndex,
        int selectionStart,
        int selectionEnd)
    {
        if (modifiers != KeyModifiers.None
            || selectionStart != selectionEnd
            || key is not (Key.Up or Key.Down))
        {
            return false;
        }

        caretIndex = Math.Clamp(caretIndex, 0, text.Length);
        if (key == Key.Up)
        {
            var firstLineEnd = text.IndexOf('\n');
            return firstLineEnd < 0 || caretIndex <= firstLineEnd;
        }

        var lastLineStart = text.LastIndexOf('\n') + 1;
        return caretIndex >= lastLineStart;
    }

    internal static bool ShouldSend(Key key, KeyModifiers modifiers) =>
        key == Key.Enter
        && modifiers is KeyModifiers.None or KeyModifiers.Control;

    private static void MoveCaretToEnd(TextBox textBox)
    {
        var end = textBox.Text?.Length ?? 0;
        textBox.CaretIndex = end;
        textBox.SelectionStart = end;
        textBox.SelectionEnd = end;
    }

    internal static bool IsNearLatest(double extentHeight, double viewportHeight, double offsetY) =>
        extentHeight <= viewportHeight
        || extentHeight - viewportHeight - offsetY <= FollowLatestThreshold;

    private void AttachMessageSource(AiAgentPageViewModel? viewModel)
    {
        if (ReferenceEquals(_messageSource, viewModel))
            return;
        if (_messageSource is not null)
        {
            _messageSource.Messages.CollectionChanged -= OnMessagesChanged;
            _messageSource.InputFocusRequested -= OnInputFocusRequested;
        }
        ClearTrackedMessages();
        _messageSource = viewModel;
        if (_messageSource is null)
            return;
        _messageSource.Messages.CollectionChanged += OnMessagesChanged;
        _messageSource.InputFocusRequested += OnInputFocusRequested;
        foreach (var message in _messageSource.Messages)
            TrackMessage(message);
    }

    private void OnInputFocusRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AiAgentInputTextBox.Focus();
            MoveCaretToEnd(AiAgentInputTextBox);
        }, DispatcherPriority.Background);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _followLatest = true;
            ClearTrackedMessages();
            if (_messageSource is not null)
            {
                foreach (var message in _messageSource.Messages)
                    TrackMessage(message);
            }
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (AiChatMessageViewModel message in e.OldItems)
                    UntrackMessage(message);
            }
            if (e.NewItems is not null)
            {
                foreach (AiChatMessageViewModel message in e.NewItems)
                    TrackMessage(message);
            }
        }
        QueueScrollToLatest();
    }

    private void TrackMessage(AiChatMessageViewModel message)
    {
        if (_trackedMessages.Add(message))
            message.PropertyChanged += OnMessagePropertyChanged;
    }

    private void UntrackMessage(AiChatMessageViewModel message)
    {
        if (_trackedMessages.Remove(message))
            message.PropertyChanged -= OnMessagePropertyChanged;
    }

    private void ClearTrackedMessages()
    {
        foreach (var message in _trackedMessages)
            message.PropertyChanged -= OnMessagePropertyChanged;
        _trackedMessages.Clear();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiChatMessageViewModel.Content)
            or nameof(AiChatMessageViewModel.Reasoning)
            or nameof(AiChatMessageViewModel.IsThinking))
        {
            QueueScrollToLatest();
        }
    }

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_autoScrolling || _scrollQueued)
            return;
        _followLatest = IsNearLatest(
            ChatScrollViewer.Extent.Height,
            ChatScrollViewer.Viewport.Height,
            ChatScrollViewer.Offset.Y);
    }

    private void QueueScrollToLatest(bool force = false)
    {
        if (force)
            _followLatest = true;
        if (!_isAttached || !_followLatest || _scrollQueued)
            return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (!_isAttached || !_followLatest)
                return;
            _autoScrolling = true;
            ChatScrollViewer.ScrollToEnd();
            Dispatcher.UIThread.Post(
                () => _autoScrolling = false,
                DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }
}
