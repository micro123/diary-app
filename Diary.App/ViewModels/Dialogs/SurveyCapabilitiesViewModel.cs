using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public partial class SurveyCapabilitiesViewModel : ViewModelBase, IDialogContext
{
    public SurveyCapabilitiesViewModel(
        IEnumerable<SurveyCapabilityResult> capabilities,
        string status)
    {
        Capabilities = capabilities
            .OrderBy(capability => capability.NodeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Status = status;
    }

    public IReadOnlyList<SurveyCapabilityResult> Capabilities { get; }
    public string Status { get; }
    public bool IsEmpty => Capabilities.Count == 0;

    [RelayCommand]
    private void Dismiss() => RequestClose?.Invoke(this, null);

    public void Close() => Dismiss();

    public event EventHandler<object?>? RequestClose;
}
