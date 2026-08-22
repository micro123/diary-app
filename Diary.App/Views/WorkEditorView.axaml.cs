using Avalonia.Controls;
using Avalonia.Interactivity;
using Diary.App.ViewModels;

namespace Diary.App.Views;

public partial class WorkEditorView : UserControl
{
    public WorkEditorView()
    {
        InitializeComponent();
    }

    private void OnWorkTimeInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkEditorViewModel viewModel
            && viewModel.ApplyTimeInputCommand.CanExecute(null))
        {
            viewModel.ApplyTimeInputCommand.Execute(null);
        }
    }
}
