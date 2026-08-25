using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Diary.App.ViewModels;

namespace Diary.App.Views;

public partial class ScriptManagementView : UserControl
{
    public ScriptManagementView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnSaveMetadataSettingsClicked(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => DefaultParameterFormView.FocusFirstError());

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || DataContext is not ScriptManagementViewModel viewModel
            || !viewModel.SaveMetadataSettingsCommand.CanExecute(null))
        {
            return;
        }
        viewModel.SaveMetadataSettingsCommand.Execute(null);
        Dispatcher.UIThread.Post(() => DefaultParameterFormView.FocusFirstError());
        e.Handled = true;
    }
}
