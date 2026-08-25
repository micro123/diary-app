using Avalonia.Controls;
using Avalonia.Input;
using Diary.App.ViewModels;

namespace Diary.App.Views;

public partial class DiaryEditorView : UserControl
{
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
