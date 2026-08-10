using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public enum OnboardingAction
{
    Start,
    OpenDatabaseSettings,
    Later,
}

[DiAutoRegister]
public partial class OnboardingViewModel : ViewModelBase, IDialogContext
{
    [RelayCommand]
    private void Start() => RequestClose?.Invoke(this, OnboardingAction.Start);

    [RelayCommand]
    private void OpenDatabaseSettings()
        => RequestClose?.Invoke(this, OnboardingAction.OpenDatabaseSettings);

    [RelayCommand]
    private void Later() => RequestClose?.Invoke(this, OnboardingAction.Later);

    public void Close() => Later();

    public event EventHandler<object?>? RequestClose;
}
