using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Diary.App.Views.Dialogs;

public partial class CopyDayView : UserControl
{
    public CopyDayView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
