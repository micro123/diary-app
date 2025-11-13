using System;
using Avalonia.Controls;
using Ursa.Controls;

namespace Diary.App.Views;

public partial class DiaryEditorView : UserControl
{
    public DiaryEditorView()
    {
        InitializeComponent();
    }

    private void Calendar_OnSelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        var calendar = sender as Calendar;
        if (e.AddedItems.Count != 0)
        {
            calendar.DisplayDate = (e.AddedItems[0]! as DateTime)!;
        }
    }
}