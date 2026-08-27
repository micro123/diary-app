using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Diary.App.ViewModels;
using Diary.GUIBase;
using Ursa.Controls;

namespace Diary.App.Views
{
    public partial class MainWindow : UrsaWindow
    {
        private ThemeVariantScope? _titleBarScope = null;
        private ThemeVariantScope? _statusBarScope = null;

        public MainWindow()
        {
            InitializeComponent();
            AddHandler(
                KeyDownEvent,
                OnPreviewDiaryDateNavigationKeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || DataContext is not MainWindowViewModel viewModel)
                return;

            if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                return;

            var index = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => (int)e.Key - (int)Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => (int)e.Key - (int)Key.NumPad1,
                _ => -1,
            };
            if (index < 0 || index >= viewModel.Pages.Count)
                return;

            viewModel.SelectedPage = viewModel.Pages[index];
            e.Handled = true;
        }

        private void OnPreviewDiaryDateNavigationKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainWindowViewModel
                {
                    CurrentPageModel: DiaryEditorViewModel diaryEditor,
                })
            {
                return;
            }

            var dateOffset = ResolveDiaryDateNavigationOffset(
                e.Key,
                e.PhysicalKey,
                e.KeyModifiers,
                isDiaryEditorVisible: true);
            if (dateOffset is not { } days)
                return;

            diaryEditor.NavigateCompactCalendarSelection(days);
            e.Handled = true;
        }

        internal static int? ResolveDiaryDateNavigationOffset(
            Key key,
            PhysicalKey physicalKey,
            KeyModifiers modifiers,
            bool isDiaryEditorVisible)
        {
            if (!isDiaryEditorVisible || modifiers != KeyModifiers.Alt)
                return null;

            int? physicalOffset = physicalKey switch
            {
                PhysicalKey.J => -1,
                PhysicalKey.K => 7,
                PhysicalKey.L => -7,
                PhysicalKey.Semicolon => 1,
                _ => null,
            };
            if (physicalOffset is not null)
                return physicalOffset;

            return key switch
            {
                Key.J => -1,
                Key.K => 7,
                Key.L => -7,
                Key.OemSemicolon => 1,
                _ => null,
            };
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
            if (t is not null)
                t.RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
            if (s is not null)
                s.RequestedThemeVariant = ActualThemeVariant;
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
