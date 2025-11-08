using System;
using Avalonia.Controls;
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

        private void StyledElement_OnActualThemeVariantChanged(object? sender, EventArgs e)
        {
            var s = _titleBarScope ??= this.FindDescendantOfType<ThemeVariantScope>();
            s!.RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }
}