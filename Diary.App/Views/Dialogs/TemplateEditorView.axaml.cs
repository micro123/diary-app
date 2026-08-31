using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Diary.App.ViewModels.Dialogs;

namespace Diary.App.Views.Dialogs;

public partial class TemplateEditorView : UserControl
{
    public TemplateEditorView()
    {
        InitializeComponent();
    }

    private void OnAddTagClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not TemplateViewModel viewModel)
            return;

        var menu = new ContextMenu
        {
            ItemsSource = viewModel.AvailableTags.Select(tag => new MenuItem
            {
                Header = tag.Name,
                Command = viewModel.AddTagCommand,
                CommandParameter = tag,
            }).ToArray(),
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(button.ContextMenu, menu))
                button.ContextMenu = null;
        };
        button.ContextMenu = menu;
        Dispatcher.UIThread.Post(() => menu.Open(button));
    }
}
