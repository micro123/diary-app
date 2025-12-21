using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Diary.App.Utils;

public static class UiUtility
{
    public static bool TreeDataGridToggleExpand(Control? clicked)
    {
        var control = clicked;
        if (control is not null)
        {
            TreeDataGridRow? row = null;
            while (control is not null)
            {
                if (control is ICommandSource)
                {
                    row = null;
                    break;
                }
                
                if (control is TreeDataGridRow r)
                {
                    row = r;
                }
                
                if (control is TreeDataGrid)
                {
                    break;
                }
                control = control.Parent as Control;
            }

            var cell = row?.TryGetCell(0);
            if (cell is TreeDataGridExpanderCell expanderCell)
            {
                expanderCell.IsExpanded = !expanderCell.IsExpanded;
                return true;
            }
        }
        return false;
    }
}