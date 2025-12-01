using CommunityToolkit.Mvvm.Input;
using Diary.Utils;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class StatisticsViewModel : ViewModelBase
{
    [RelayCommand]
    private void TestApi()
    {
        var Db = App.Current.UseDb!;
        var testing = Db.GetStatistics();

        int a = 0;
    }
}