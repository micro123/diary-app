using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Diary.App.ViewModels;

namespace Diary.App.Controls;

public partial class WorkRecordCalendar : UserControl
{
    private bool _calendarPointerHandlerAttached;

    public WorkRecordCalendar()
    {
        InitializeComponent();
    }

    private void OnHeaderContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel viewModel
            && sender is Control { ContextMenu: { } contextMenu } control)
        {
            if (contextMenu.IsOpen)
            {
                args.Handled = true;
                return;
            }

            OpenPeriodContextMenu(viewModel, control, contextMenu);
            args.Handled = true;
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel viewModel
            && sender is Control { ContextMenu: { } contextMenu } control
            && args.GetCurrentPoint(control).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            OpenPeriodContextMenu(viewModel, control, contextMenu);
            args.Handled = true;
        }
    }

    private void OnHeaderKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel viewModel
            && sender is Control { ContextMenu: { } contextMenu } control
            && args.Key == Key.F10
            && args.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            OpenPeriodContextMenu(viewModel, control, contextMenu);
            args.Handled = true;
        }
    }

    private void OnCalendarFlyoutOpened(object? sender, EventArgs args)
    {
        if (DataContext is not DiaryEditorViewModel viewModel
            || this.FindControl<Calendar>("DiaryCalendar") is not { } calendar)
        {
            return;
        }

        if (!_calendarPointerHandlerAttached)
        {
            calendar.AddHandler(
                InputElement.PointerReleasedEvent,
                OnFullCalendarPointerReleased,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
            _calendarPointerHandlerAttached = true;
        }

        ResetFullCalendar(calendar, viewModel.SelectedDate);
    }

    private void OnFullCalendarPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (sender is not Calendar calendar
            || args.Source is not Visual source
            || source.FindAncestorOfType<CalendarDayButton>(includeSelf: true) is null
            || calendar.SelectedDate is not { } selectedDate
            || DataContext is not DiaryEditorViewModel viewModel)
        {
            return;
        }

        viewModel.SelectCompactCalendarDateCommand.Execute(selectedDate.Date);
        Dispatcher.UIThread.Post(() =>
        {
            ResetFullCalendar(calendar, viewModel.SelectedDate);
            this.FindControl<Button>("CompactCalendarHeader")?.Flyout?.Hide();
        });
    }

    private static void ResetFullCalendar(Calendar calendar, DateTime selectedDate)
    {
        calendar.DisplayMode = CalendarMode.Month;
        calendar.SetCurrentValue(Calendar.DisplayDateProperty, selectedDate.Date);
        calendar.SetCurrentValue(Calendar.SelectedDateProperty, selectedDate.Date);
    }

    private static void OpenPeriodContextMenu(
        DiaryEditorViewModel viewModel,
        Control control,
        ContextMenu contextMenu)
    {
        viewModel.ShowCompactCalendarPeriodContextMenu();
        contextMenu.ItemsSource = viewModel.QuickMenuItems;
        contextMenu.Open(control);
    }

    private void OnDayContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel viewModel
            && sender is Control
            {
                DataContext: CompactCalendarDay day,
                ContextMenu: { } contextMenu,
            })
        {
            if (!viewModel.ShowCompactCalendarDayContextMenu(day.Date))
            {
                args.Handled = true;
                return;
            }

            contextMenu.ItemsSource = viewModel.QuickMenuItems;
        }
    }

    private void OnCalendarKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not DiaryEditorViewModel viewModel)
            return;

        var handled = true;
        switch (args.Key)
        {
            case Key.Left:
                viewModel.NavigateCompactCalendarSelection(-1);
                break;
            case Key.Right:
                viewModel.NavigateCompactCalendarSelection(1);
                break;
            case Key.Up:
                viewModel.NavigateCompactCalendarSelection(-7);
                break;
            case Key.Down:
                viewModel.NavigateCompactCalendarSelection(7);
                break;
            case Key.PageUp:
                viewModel.ShiftCompactCalendarPeriod(-1);
                break;
            case Key.PageDown:
                viewModel.ShiftCompactCalendarPeriod(1);
                break;
            default:
                handled = false;
                break;
        }

        args.Handled = handled;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs args)
    {
        if (DataContext is not DiaryEditorViewModel viewModel || args.Delta.Y == 0)
            return;

        viewModel.ShiftCompactCalendarPeriod(args.Delta.Y > 0 ? -1 : 1);
        args.Handled = true;
    }

    private void OnDayClick(object? sender, RoutedEventArgs args)
    {
        this.FindControl<ItemsControl>("CompactCalendarDays")?.Focus();
    }
}
