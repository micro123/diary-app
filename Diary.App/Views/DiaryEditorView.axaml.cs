using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Diary.App.ViewModels;

namespace Diary.App.Views;

public partial class DiaryEditorView : UserControl
{
    private bool _diaryCalendarPointerHandlerAttached;

    public DiaryEditorView()
    {
        InitializeComponent();
    }

    private void OnCompactCalendarHeaderContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel vm
            && sender is Control { ContextMenu: { } contextMenu } control)
        {
            OpenCompactCalendarPeriodContextMenu(vm, control, contextMenu);
            args.Handled = true;
        }
    }

    private void OnCompactCalendarHeaderPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel vm
            && sender is Control { ContextMenu: { } contextMenu } control
            && args.GetCurrentPoint(control).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            OpenCompactCalendarPeriodContextMenu(vm, control, contextMenu);
            args.Handled = true;
        }
    }

    private void OnCompactCalendarHeaderKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel vm
            && sender is Control { ContextMenu: { } contextMenu } control
            && args.Key == Key.F10
            && args.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            OpenCompactCalendarPeriodContextMenu(vm, control, contextMenu);
            args.Handled = true;
        }
    }

    private void OnDiaryCalendarFlyoutOpened(object? sender, EventArgs args)
    {
        if (DataContext is not DiaryEditorViewModel vm
            || this.FindControl<Calendar>("DiaryCalendar") is not { } calendar)
        {
            return;
        }

        if (!_diaryCalendarPointerHandlerAttached)
        {
            calendar.AddHandler(
                InputElement.PointerReleasedEvent,
                OnDiaryCalendarPointerReleased,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
            _diaryCalendarPointerHandlerAttached = true;
        }

        ResetDiaryCalendar(calendar, vm.SelectedDate);
    }

    private void OnDiaryCalendarPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (sender is not Calendar calendar
            || args.Source is not Visual source
            || source.FindAncestorOfType<CalendarDayButton>(includeSelf: true) is null
            || calendar.SelectedDate is not { } selectedDate
            || DataContext is not DiaryEditorViewModel vm)
        {
            return;
        }

        vm.SelectCompactCalendarDateCommand.Execute(selectedDate.Date);

        Dispatcher.UIThread.Post(() =>
        {
            ResetDiaryCalendar(calendar, vm.SelectedDate);
            this.FindControl<Button>("CompactCalendarHeader")?.Flyout?.Hide();
        });
    }

    private static void ResetDiaryCalendar(Calendar calendar, DateTime selectedDate)
    {
        calendar.DisplayMode = CalendarMode.Month;
        calendar.SetCurrentValue(Calendar.DisplayDateProperty, selectedDate.Date);
        calendar.SetCurrentValue(Calendar.SelectedDateProperty, selectedDate.Date);
    }

    private static void OpenCompactCalendarPeriodContextMenu(
        DiaryEditorViewModel viewModel,
        Control control,
        ContextMenu contextMenu)
    {
        viewModel.ShowCompactCalendarPeriodContextMenu();
        contextMenu.ItemsSource = viewModel.QuickMenuItems;
        contextMenu.Open(control);
    }

    private void OnCompactCalendarDayContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel vm
            && sender is Control
            {
                DataContext: CompactCalendarDay day,
                ContextMenu: { } contextMenu,
            })
        {
            if (!vm.ShowCompactCalendarDayContextMenu(day.Date))
            {
                args.Handled = true;
                return;
            }

            contextMenu.ItemsSource = vm.QuickMenuItems;
        }
    }

    private void OnCompactCalendarKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not DiaryEditorViewModel vm)
            return;

        var handled = true;
        switch (args.Key)
        {
            case Key.Left:
                vm.NavigateCompactCalendarSelection(-1);
                break;
            case Key.Right:
                vm.NavigateCompactCalendarSelection(1);
                break;
            case Key.Up:
                vm.NavigateCompactCalendarSelection(-7);
                break;
            case Key.Down:
                vm.NavigateCompactCalendarSelection(7);
                break;
            case Key.PageUp:
                vm.ShiftCompactCalendarPeriod(-1);
                break;
            case Key.PageDown:
                vm.ShiftCompactCalendarPeriod(1);
                break;
            default:
                handled = false;
                break;
        }

        args.Handled = handled;
    }

    private void OnCompactCalendarPointerWheelChanged(object? sender, PointerWheelEventArgs args)
    {
        if (DataContext is not DiaryEditorViewModel vm || args.Delta.Y == 0)
            return;

        vm.ShiftCompactCalendarWeeks(args.Delta.Y > 0 ? -1 : 1);
        args.Handled = true;
    }

    private void OnCompactCalendarDayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        this.FindControl<ItemsControl>("CompactCalendarDays")?.Focus();
    }

}
