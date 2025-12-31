using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Diary.GUIBase.ViewModels;

namespace Diary.GUIBase;

public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var sourceType = param.GetType();
        var name = sourceType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = sourceType.Assembly.GetType(name);

        if (type != null)
        {
            var control = (Control)Activator.CreateInstance(type)!;
            var vm = param as ViewModelBase;
            vm!.SetView(control);
            LastVm = vm;
            return control;
        }

        return new TextBlock
        {
            Text = "Not Found: " + name,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }

    private ViewModelBase? _lastVm;
    private ViewModelBase? LastVm
    {
        set
        {
            _lastVm?.OnHide();
            _lastVm = value;
            _lastVm?.OnShow();
        }
    }
}