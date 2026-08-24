using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Diary.App.ViewModels;

namespace Diary.App.Views;

public partial class DiaryEditorView : UserControl
{
    public DiaryEditorView()
    {
        InitializeComponent();
    }

    private void OnCalendarContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (DataContext is not DiaryEditorViewModel vm)
            return;

        Button? btn = null;
        Calendar? calendar = null;
        DiaryEditorViewModel.CalendarWhat what = DiaryEditorViewModel.CalendarWhat.None;
        DateTime? selectDate = null;

        bool isHeader = false;
        bool isGridButton = false;

        var control = args.Source as Control;
        while (control is not null)
        {
            if (btn is null)
            {
                if (control is CalendarDayButton d)
                {
                    what = DiaryEditorViewModel.CalendarWhat.Day;
                    btn = d;
                    selectDate = (DateTime)d.DataContext!;
                }
                else if (control is Button m && control.Name == "PART_HeaderButton")
                {
                    what = DiaryEditorViewModel.CalendarWhat.None;
                    btn = m;
                    isHeader = true;
                }
                else if (control is CalendarButton y)
                {
                    what = DiaryEditorViewModel.CalendarWhat.None;
                    btn = y;
                    isGridButton = true;
                }
            }

            if (control is Calendar c)
            {
                calendar = c;
                break;
            }

            control = control.Parent as Control;
        }

        if (what == DiaryEditorViewModel.CalendarWhat.None)
        {
            if (isHeader)
            {
                switch (calendar!.DisplayMode)
                {
                    case CalendarMode.Month:
                        what = DiaryEditorViewModel.CalendarWhat.Month;
                        selectDate = calendar.DisplayDate.AddDays(-calendar.DisplayDate.Day + 1);
                        break;
                    case CalendarMode.Year:
                        what = DiaryEditorViewModel.CalendarWhat.Year;
                        selectDate = new DateTime(calendar.DisplayDate.Year, 1, 1);
                        break;
                }
            }
            else if (isGridButton)
            {
                switch (calendar!.DisplayMode)
                {
                    case CalendarMode.Year:
                        what = DiaryEditorViewModel.CalendarWhat.Month;
                        break;
                    case CalendarMode.Decade:
                        what = DiaryEditorViewModel.CalendarWhat.Year;
                        break;
                }

                selectDate = (DateTime)btn!.DataContext!;
            }
        }

        if (what == DiaryEditorViewModel.CalendarWhat.None)
        {
            args.Handled = true;
            return;
        }

        vm.ShowCalendarContextMenu((DateTime)selectDate!, what);
        if (calendar?.ContextMenu is { } contextMenu)
            contextMenu.ItemsSource = vm.QuickMenuItems;
    }

    private void OnCompactCalendarDayContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (DataContext is DiaryEditorViewModel vm
            && sender is Control
            {
                DataContext: CompactCalendarDay day,
                ContextMenu: { } contextMenu,
            } control)
        {
            vm.ShowCalendarContextMenu(day.Date, DiaryEditorViewModel.CalendarWhat.Day);
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
