using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
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

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ShouldSend(e.Key, e.KeyModifiers)
            || DataContext is not AiAgentPageViewModel viewModel
            || !viewModel.SendCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        viewModel.SendCommand.Execute(null);
    }

    internal static bool ShouldSend(Key key, KeyModifiers modifiers) =>
        key == Key.Enter
        && modifiers is KeyModifiers.None or KeyModifiers.Control;

    internal static bool IsNearLatest(double extentHeight, double viewportHeight, double offsetY) =>
        extentHeight <= viewportHeight
        || extentHeight - viewportHeight - offsetY <= FollowLatestThreshold;

    private void AttachMessageSource(AiAgentPageViewModel? viewModel)
    {
        if (ReferenceEquals(_messageSource, viewModel))
            return;
        if (_messageSource is not null)
            _messageSource.Messages.CollectionChanged -= OnMessagesChanged;
        ClearTrackedMessages();
        _messageSource = viewModel;
        if (_messageSource is null)
            return;
        _messageSource.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var message in _messageSource.Messages)
            TrackMessage(message);
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
