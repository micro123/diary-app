using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Diary.App.ViewModels.Dialogs;

namespace Diary.App.Views.Dialogs;

public partial class ScriptRunDialogView : UserControl
{
    public ScriptRunDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnRunClicked(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => ParameterFormView.FocusFirstError());

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ScriptRunDialogViewModel viewModel)
            return;
        if (e.Key == Key.Escape)
        {
            viewModel.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }
        var submit = e.Key == Key.Enter
            && (e.KeyModifiers.HasFlag(KeyModifiers.Control)
                || e.Source is TextBox { AcceptsReturn: false });
        if (!submit || !viewModel.RunCommand.CanExecute(null))
            return;
        viewModel.RunCommand.Execute(null);
        Dispatcher.UIThread.Post(() => ParameterFormView.FocusFirstError());
        e.Handled = true;
    }
}
