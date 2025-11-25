using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App;

[DiAutoRegister]
public partial class AppModel: ObservableObject
{
    private readonly ILogger _logger;

    public AppModel(ILogger logger)
    {
        _logger = logger;
    }


    [RelayCommand]
    private void QuitApp()
    {
        _logger.LogInformation("Quitting app");
    }

    [RelayCommand]
    private void ShowAbout()
    {
        _logger.LogInformation("Showing about");
    }

    [RelayCommand]
    private void RaiseWindow()
    {
        _logger.LogInformation("Raise window");
    }
}