using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record CopyDaySelection(DateTime SourceDate);

public partial class CopyDayViewModel : ViewModelBase, IDialogContext
{
    private readonly DateTime _targetDate;

    [ObservableProperty]
    private DateTime _sourceDate;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public string TargetDateText => _targetDate.ToString("yyyy-MM-dd");

    public CopyDayViewModel(DateTime targetDate)
    {
        _targetDate = targetDate.Date;
        SourceDate = _targetDate.AddDays(-1);
    }

    [RelayCommand]
    private void Confirm()
    {
        if (SourceDate.Date == _targetDate)
        {
            ValidationMessage = "源日期不能与目标日期相同。";
            return;
        }

        RequestClose?.Invoke(this, new CopyDaySelection(SourceDate.Date));
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, null);

    public void Close() => Cancel();

    public event EventHandler<object?>? RequestClose;
}
