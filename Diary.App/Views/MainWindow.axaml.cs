using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Ursa.Controls;
using Notification = Ursa.Controls.Notification;
using WindowNotificationManager = Ursa.Controls.WindowNotificationManager;

namespace Diary.App.Views
{
    public partial class MainWindow: UrsaWindow
    {
        private ThemeVariantScope? _titleBarScope = null;
        private ThemeVariantScope? _statusBarScope = null;
        
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnActualThemeVariantChanged(object? sender, EventArgs e)
        {
            SyncBarsTheme();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SyncBarsTheme();
            var host = this.FindDescendantOfType<OverlayDialogHost>();
            host?.DialogDataTemplates.Add(new ViewLocator());
        }

        private void SyncBarsTheme()
        {
            var t = _titleBarScope ??= GetThemeScopeOf<TitleBar>();
            var s = _statusBarScope ??= GetThemeScopeOf<StatusBarView>();
            t!.RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
            s!.RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        private ThemeVariantScope? GetThemeScopeOf<T>() where T : Control
        {
            var control = this.FindDescendantOfType<T>();
            if (control != null)
            {
                return control.FindDescendantOfType<ThemeVariantScope>();
            }
            return null;
        }
    }
}