using Avalonia.Controls;
using Avalonia.VisualTree;
using Diary.App.Models;

namespace Diary.App.Views;

public partial class ScriptParameterFormView : UserControl
{
    public ScriptParameterFormView() => InitializeComponent();

    public bool FocusFirstError()
    {
        if (DataContext is not ScriptParameterFormViewModel form)
            return false;
        var field = form.Fields.FirstOrDefault(item => item.HasError);
        if (field is null)
            return false;
        var control = this.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, field)
                && item.Focusable
                && item.IsVisible
                && item is not Button);
        if (control is null)
            return false;
        control.BringIntoView();
        return control.Focus();
    }
}
