using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Diary.App.ViewModels;

namespace Diary.App.Views;

public partial class WorkEditorView : UserControl
{
    private WorkEditorViewModel? _attachedViewModel;

    public WorkEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnWorkEditorDataContextChanged;
    }

    private void OnWorkEditorDataContextChanged(object? sender, EventArgs args)
    {
        var nextViewModel = DataContext as WorkEditorViewModel;
        _attachedViewModel = RebindViewModel(this, _attachedViewModel, nextViewModel);
    }

    internal static WorkEditorViewModel? RebindViewModel(
        Control view,
        WorkEditorViewModel? previousViewModel,
        WorkEditorViewModel? nextViewModel)
    {
        if (ReferenceEquals(previousViewModel, nextViewModel))
            return previousViewModel;

        previousViewModel?.SetView(null);
        nextViewModel?.SetView(view);
        return nextViewModel;
    }

    private void OnWorkTimeInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkEditorViewModel viewModel
            && viewModel.ApplyTimeInputCommand.CanExecute(null))
        {
            viewModel.ApplyTimeInputCommand.Execute(null);
        }
    }

    private void OnUpdateFromTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not WorkEditorViewModel viewModel)
            return;

        OpenTemplateMenu(button, viewModel, applyTemplate: false);
    }

    private void OnApplyTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not WorkEditorViewModel viewModel)
            return;

        OpenTemplateMenu(button, viewModel, applyTemplate: true);
    }

    private void OnAddTagClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not WorkEditorViewModel viewModel)
            return;

        OpenContextMenu(
            button,
            viewModel.AvailableTags.Select(tag => new MenuItem
            {
                Header = tag.Name,
                Command = viewModel.AddTagCommand,
                CommandParameter = tag,
            }));
    }

    private static void OpenTemplateMenu(
        Button anchor,
        WorkEditorViewModel viewModel,
        bool applyTemplate)
    {
        var command = applyTemplate
            ? viewModel.ApplyTemplateCommand
            : viewModel.UpdateFromTemplateCommand;
        OpenContextMenu(
            anchor,
            viewModel.Templates.Select(template => new MenuItem
            {
                Header = template.Name,
                Command = command,
                CommandParameter = template,
            }));
    }

    private static void OpenContextMenu(Control anchor, IEnumerable<MenuItem> items)
    {
        var menu = new ContextMenu { ItemsSource = items.ToArray() };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(anchor.ContextMenu, menu))
                anchor.ContextMenu = null;
        };
        anchor.ContextMenu = menu;
        Dispatcher.UIThread.Post(() => menu.Open(anchor));
    }

}
