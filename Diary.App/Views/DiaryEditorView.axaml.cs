using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Diary.App.ViewModels;

namespace Diary.App.Views;

public partial class DiaryEditorView : UserControl
{
    private static readonly TimeSpan WorkEditorPreloadDelay = TimeSpan.FromMilliseconds(250);
    private CancellationTokenSource? _workEditorPreloadCancellation;

    public DiaryEditorView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ScheduleWorkEditorPreload();
        DetachedFromVisualTree += (_, _) => CancelWorkEditorPreload();
    }

    private void ScheduleWorkEditorPreload()
    {
        CancelWorkEditorPreload();
        _workEditorPreloadCancellation = new CancellationTokenSource();
        _ = PreloadWorkEditorAfterIdleAsync(_workEditorPreloadCancellation.Token);
    }

    private async Task PreloadWorkEditorAfterIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(WorkEditorPreloadDelay, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (DataContext is not DiaryEditorViewModel viewModel
                    || this.FindControl<ScrollViewer>("WorkEditorScrollViewer") is not { } host
                    || viewModel.HasItem
                    || viewModel.IsWorkEditorPrewarmed
                    || !viewModel.TryCreateWorkEditorPreloadModel())
                {
                    return;
                }

                if (this.FindControl<Panel>("WorkEditorPanel") is { Bounds.Width: > 0, Bounds.Height: > 0 } panel)
                {
                    host.Measure(panel.Bounds.Size);
                    host.Arrange(new Rect(panel.Bounds.Size));
                }
            }, DispatcherPriority.Background);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (this.FindControl<ScrollViewer>("WorkEditorScrollViewer") is not { } host
                    || this.FindControl<Panel>("WorkEditorPanel") is not { Bounds.Width: > 0, Bounds.Height: > 0 } panel)
                {
                    return;
                }

                host.Measure(panel.Bounds.Size);
                host.Arrange(new Rect(panel.Bounds.Size));
            }, DispatcherPriority.Render);
        }
        catch (OperationCanceledException)
        {
            // 页面卸载时取消尚未执行的后台预实现。
        }
        catch (Exception exception)
        {
            if (DataContext is DiaryEditorViewModel viewModel)
                viewModel.LogWorkEditorPreloadFailure(exception);
        }
    }

    private void CancelWorkEditorPreload()
    {
        _workEditorPreloadCancellation?.Cancel();
        _workEditorPreloadCancellation?.Dispose();
        _workEditorPreloadCancellation = null;
    }
}
