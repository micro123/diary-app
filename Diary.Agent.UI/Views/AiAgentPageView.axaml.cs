using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Diary.Agent.UI.ViewModels;

namespace Diary.Agent.UI.Views;

public partial class AiAgentPageView : UserControl
{
    public AiAgentPageView()
    {
        InitializeComponent();
        AiAgentInputTextBox.AddHandler(
            KeyDownEvent,
            OnInputKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
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
}
