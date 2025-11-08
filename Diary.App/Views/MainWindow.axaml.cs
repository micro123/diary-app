using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Ursa.Controls;

namespace Diary.App.Views
{
    public partial class MainWindow: UrsaWindow
    {
        private ThemeVariantScope? _titleBarScope = null;
        
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnActualThemeVariantChanged(object? sender, EventArgs e)
        {
            SyncTitleBarTheme();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SyncTitleBarTheme();
        }

        private void SyncTitleBarTheme()
        {
            var s = _titleBarScope ??= this.FindDescendantOfType<ThemeVariantScope>();
            s!.RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }
}