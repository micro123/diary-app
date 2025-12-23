using Diary.App.ViewModels;

namespace Diary.App.Models;

public class NavigateInfo(string name, string icon, ViewModelBase? vm, string gesture = "")
{
    public string Name { get; init; } = name;
    public string Icon { get; init; } = icon;
    public ViewModelBase? ViewModel { get; init; } = vm;
    
    public string Gesture { get; init; } = gesture;
}