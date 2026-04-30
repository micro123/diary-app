using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Diary.GUIBase.ViewModels;

public abstract partial class PaginatedSearchViewModel<T> : ViewModelBase
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand), nameof(LastPageCommand), nameof(PrevPageCommand), nameof(NextPageCommand))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand), nameof(LastPageCommand), nameof(PrevPageCommand), nameof(NextPageCommand))]
    private int _totalPage = 1;

    [ObservableProperty] private ObservableCollection<T> _searchResults = new();
    [ObservableProperty] private int _resultCount;

    protected abstract int PageSize { get; }

    protected abstract Task<(bool ok, IEnumerable<T>? results, int total)> ExecuteSearchAsync(int page);

    protected void UpdateSearchResults(IEnumerable<T>? items, int total)
    {
        ResultCount = total;
        TotalPage = total / PageSize + 1;
        SearchResults.Clear();
        if (items == null) return;
        foreach (var item in items)
            SearchResults.Add(item);
    }

    protected async Task DoSearchInternalAsync()
    {
        var (ok, results, total) = await ExecuteSearchAsync(CurrentPage - 1);
        if (!ok)
            NotificationManager?.Show("似乎有什么出错了 >_!", NotificationType.Error);
        await Dispatcher.UIThread.InvokeAsync(() => UpdateSearchResults(results, total));
    }

    [RelayCommand(CanExecute = nameof(CanGoFirstPage))]
    private async Task FirstPage()
    {
        CurrentPage = 1;
        await DoSearchInternalAsync();
    }

    private bool CanGoFirstPage => CurrentPage != 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevPage))]
    private async Task PrevPage()
    {
        CurrentPage -= 1;
        await DoSearchInternalAsync();
    }

    private bool CanGoPrevPage => CurrentPage > 1;

    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    private async Task NextPage()
    {
        CurrentPage += 1;
        await DoSearchInternalAsync();
    }

    private bool CanGoNextPage => CurrentPage != TotalPage;

    [RelayCommand(CanExecute = nameof(CanGoLastPage))]
    private async Task LastPage()
    {
        CurrentPage = TotalPage;
        await DoSearchInternalAsync();
    }

    private bool CanGoLastPage => CurrentPage < TotalPage;
}
