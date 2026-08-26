using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Diary.App.Models;
using Diary.Core.Constants;
using Diary.GUIBase;
using Diary.GUIBase.ViewModels;

namespace Diary.App.Controls;

public sealed class NavigationViewHost : Grid
{
    public static readonly StyledProperty<ViewModelBase?> CurrentPageProperty =
        AvaloniaProperty.Register<NavigationViewHost, ViewModelBase?>(nameof(CurrentPage));

    public static readonly StyledProperty<IEnumerable<NavigateInfo>?> PagesProperty =
        AvaloniaProperty.Register<NavigationViewHost, IEnumerable<NavigateInfo>?>(nameof(Pages));

    private readonly Dictionary<ViewModelBase, Control> _views = new();
    private CancellationTokenSource? _preloadCancellation;
    private INotifyCollectionChanged? _observablePages;
    private ViewModelBase? _activePage;
    private Control? _activeView;
    private bool _isAttached;

    public NavigationViewHost()
    {
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            ActivateCurrentPage();
            SchedulePreload();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            CancelPreload();
            _activePage?.OnHide();
            _activePage = null;
            _activeView = null;
        };
    }

    public ViewModelBase? CurrentPage
    {
        get => GetValue(CurrentPageProperty);
        set => SetValue(CurrentPageProperty, value);
    }

    public IEnumerable<NavigateInfo>? Pages
    {
        get => GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CurrentPageProperty)
        {
            ActivateCurrentPage();
            PruneDetachedPages();
        }
        else if (change.Property == PagesProperty)
        {
            SubscribeToPages();
            PruneDetachedPages();
            SchedulePreload();
        }
    }

    internal async Task PreloadNowAsync(CancellationToken cancellationToken = default)
    {
        var currentPage = CurrentPage;
        var pages = Pages?
            .Where(page => page.ViewModel is { IsViewCacheable: true }
                           && !ReferenceEquals(page.ViewModel, currentPage))
            .OrderBy(page => GetPreloadPriority(page.Name))
            .ToArray() ?? [];

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Control? view = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_isAttached || page.ViewModel is null || ReferenceEquals(page.ViewModel, CurrentPage))
                    return;
                view = GetOrCreateView(page.ViewModel);
                PrepareForPreload(view);
            }, DispatcherPriority.Background);

            if (view is null)
                continue;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Bounds.Width > 0 && Bounds.Height > 0)
                {
                    view.Measure(Bounds.Size);
                    view.Arrange(new Rect(Bounds.Size));
                }
            }, DispatcherPriority.Render);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(view, _activeView))
                    PrepareAsActive(view);
                else
                    PrepareAsInactive(view);
            }, DispatcherPriority.Background);

            await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken);
        }
    }

    private void ActivateCurrentPage()
    {
        if (!_isAttached || ReferenceEquals(_activePage, CurrentPage))
            return;

        var previousPage = _activePage;
        var previousView = _activeView;
        _activePage = CurrentPage;
        _activeView = null;

        previousPage?.OnHide();
        if (previousView is not null)
        {
            if (previousPage?.IsViewCacheable == true)
                PrepareAsInactive(previousView);
            else
                Children.Remove(previousView);
        }

        if (_activePage is null)
            return;

        _activeView = GetOrCreateView(_activePage);
        PrepareAsActive(_activeView);
        _activePage.OnShow();
    }

    private Control GetOrCreateView(ViewModelBase viewModel)
    {
        if (viewModel.IsViewCacheable && _views.TryGetValue(viewModel, out var cachedView))
            return cachedView;

        var view = ViewLocator.ResolveView(viewModel, viewModel.IsViewCacheable);
        if (view.Parent is not null && !ReferenceEquals(view.Parent, this))
            throw new InvalidOperationException($"{viewModel.GetType().FullName} 的缓存 View 已属于其他视觉宿主。");
        if (!Children.Contains(view))
            Children.Add(view);
        if (viewModel.IsViewCacheable)
            _views[viewModel] = view;
        return view;
    }

    private static void PrepareForPreload(Control view)
    {
        view.IsVisible = true;
        view.Opacity = 0;
        view.IsHitTestVisible = false;
        view.SetValue(Panel.ZIndexProperty, 0);
    }

    private static void PrepareAsInactive(Control view)
    {
        view.IsVisible = false;
        view.Opacity = 1;
        view.IsHitTestVisible = false;
        view.SetValue(Panel.ZIndexProperty, 0);
    }

    private static void PrepareAsActive(Control view)
    {
        view.IsVisible = true;
        view.Opacity = 1;
        view.IsHitTestVisible = true;
        view.SetValue(Panel.ZIndexProperty, 1);
    }

    private void SubscribeToPages()
    {
        if (_observablePages is not null)
            _observablePages.CollectionChanged -= OnPagesCollectionChanged;
        _observablePages = Pages as INotifyCollectionChanged;
        if (_observablePages is not null)
            _observablePages.CollectionChanged += OnPagesCollectionChanged;
    }

    private void OnPagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PruneDetachedPages();
        SchedulePreload();
    }

    private void PruneDetachedPages()
    {
        var activeModels = Pages?
            .Select(page => page.ViewModel)
            .OfType<ViewModelBase>()
            .ToHashSet() ?? [];
        if (CurrentPage is not null)
            activeModels.Add(CurrentPage);

        foreach (var removed in _views.Keys.Where(viewModel => !activeModels.Contains(viewModel)).ToArray())
        {
            Children.Remove(_views[removed]);
            _views.Remove(removed);
        }
    }

    private void SchedulePreload()
    {
        CancelPreload();
        if (!_isAttached)
            return;
        _preloadCancellation = new CancellationTokenSource();
        _ = PreloadAfterIdleAsync(_preloadCancellation.Token);
    }

    private async Task PreloadAfterIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            await PreloadNowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 页面集合更新或宿主卸载会取消旧预热任务。
        }
    }

    private void CancelPreload()
    {
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = null;
    }

    private static int GetPreloadPriority(string pageName) => pageName switch
    {
        PageNames.Statistics => 0,
        PageNames.Scripts => 1,
        PageNames.WorkItemQuery => 2,
        PageNames.SurveyTool => 3,
        _ => 4,
    };
}
