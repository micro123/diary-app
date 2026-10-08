using Diary.GUIBase.ViewModels;

namespace Diary.ModuleUI;

public interface INavigationContribution
{
    string Id { get; }

    string Title { get; }

    string Icon { get; }

    int Order { get; }

    ViewModelBase CreateViewModel(IServiceProvider services);
}

public interface ISettingsContribution
{
    string Id { get; }

    string Title { get; }

    int Order { get; }

    ViewModelBase CreateViewModel(IServiceProvider services);
}

public sealed record NavigationContributionPage(
    string Id,
    string Title,
    string Icon,
    int Order,
    ViewModelBase ViewModel);

public sealed record SettingsContributionPage(
    string Id,
    string Title,
    int Order,
    ViewModelBase ViewModel);

public sealed class NavigationContributionRegistry
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<INavigationContribution> _contributions;
    private readonly Dictionary<string, ViewModelBase> _viewModels = new(StringComparer.Ordinal);

    public NavigationContributionRegistry(
        IServiceProvider services,
        IEnumerable<INavigationContribution> contributions)
    {
        _services = services;
        _contributions = contributions
            .GroupBy(contribution => contribution.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(contribution => contribution.Order)
            .ThenBy(contribution => contribution.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<NavigationContributionPage> GetPages() => _contributions
        .Select(contribution => new NavigationContributionPage(
            contribution.Id,
            contribution.Title,
            contribution.Icon,
            contribution.Order,
            GetOrCreate(contribution)))
        .ToArray();

    private ViewModelBase GetOrCreate(INavigationContribution contribution)
    {
        if (_viewModels.TryGetValue(contribution.Id, out var viewModel))
            return viewModel;
        viewModel = contribution.CreateViewModel(_services);
        _viewModels.Add(contribution.Id, viewModel);
        return viewModel;
    }
}

public sealed class SettingsContributionRegistry
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<ISettingsContribution> _contributions;
    private readonly Dictionary<string, ViewModelBase> _viewModels = new(StringComparer.Ordinal);

    public SettingsContributionRegistry(
        IServiceProvider services,
        IEnumerable<ISettingsContribution> contributions)
    {
        _services = services;
        _contributions = contributions
            .GroupBy(contribution => contribution.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(contribution => contribution.Order)
            .ThenBy(contribution => contribution.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<SettingsContributionPage> GetPages() => _contributions
        .Select(contribution => new SettingsContributionPage(
            contribution.Id,
            contribution.Title,
            contribution.Order,
            GetOrCreate(contribution)))
        .ToArray();

    private ViewModelBase GetOrCreate(ISettingsContribution contribution)
    {
        if (_viewModels.TryGetValue(contribution.Id, out var viewModel))
            return viewModel;
        viewModel = contribution.CreateViewModel(_services);
        _viewModels.Add(contribution.Id, viewModel);
        return viewModel;
    }
}
