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
    }
}
