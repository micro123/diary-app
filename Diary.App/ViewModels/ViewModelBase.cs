using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Diary.App.ViewModels;

public class ViewModelBase : ObservableObject
{
    public Control? View { get; set; }
}