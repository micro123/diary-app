using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Diary.GUIBase.ViewModels;
using System.Runtime.CompilerServices;

namespace Diary.GUIBase;

public sealed class ViewLocator : IDataTemplate
{
    private static readonly ConditionalWeakTable<ViewModelBase, Control> CachedViews = new();

    public bool CacheViews { get; set; }

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        if (param is not ViewModelBase viewModel)
            return CreateMissingView(param.GetType());

        var control = ResolveView(viewModel, CacheViews);
        LastVm = viewModel;
        return control;
    }

    public static Control ResolveView(ViewModelBase viewModel, bool useCache)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return useCache && viewModel.IsViewCacheable
            ? GetOrCreateCachedView(viewModel)
            : CreateView(viewModel);
    }

    public static Control PreloadCached(ViewModelBase viewModel, Size? availableSize = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (!viewModel.IsViewCacheable)
            throw new InvalidOperationException($"{viewModel.GetType().FullName} 未声明可缓存 View。");
        var control = GetOrCreateCachedView(viewModel);
        if (availableSize is { } size && size.Width > 0 && size.Height > 0)
        {
            control.Measure(size);
            control.Arrange(new Rect(size));
        }
        return control;
    }

    private static Control GetOrCreateCachedView(ViewModelBase viewModel)
        => CachedViews.GetValue(viewModel, CreateView);

    private static Control CreateView(ViewModelBase viewModel)
    {
        var sourceType = viewModel.GetType();
        var name = sourceType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = sourceType.Assembly.GetType(name);

        if (type != null)
        {
            var control = (Control)Activator.CreateInstance(type)!;
            control.DataContext = viewModel;
            viewModel.SetView(control);
            return control;
        }

        return CreateMissingView(sourceType);
    }

    private static TextBlock CreateMissingView(Type sourceType)
    {
        var name = sourceType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
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
            if (ReferenceEquals(_lastVm, value))
                return;
            _lastVm?.OnHide();
            _lastVm = value;
            _lastVm?.OnShow();
        }
    }
}
