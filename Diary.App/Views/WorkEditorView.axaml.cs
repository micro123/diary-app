using Avalonia.Controls;
using Avalonia.Interactivity;
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
        if (sender is not SplitButton button || DataContext is not WorkEditorViewModel viewModel)
            return;

        var menu = new MenuFlyout
        {
            ItemsSource = viewModel.Templates.Select(template => new MenuItem
            {
                Header = template.Name,
                Command = viewModel.UpdateFromTemplateCommand,
                CommandParameter = template,
            }).ToArray(),
        };
        menu.ShowAt(button);
    }

}
