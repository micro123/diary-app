using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.Configure;
using Diary.Utils;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableCollection<object> _settingsTree = new();

    public SettingsViewModel()
    {
        BuildTree();
    }

    private void BuildTree()
    {
        var config = App.Current.AppConfig;
        BuildTree(SettingsTree, config);
    }

    private void BuildTree(ObservableCollection<object> tree, object o)
    {
        var type = o.GetType();
        var properties = type.GetProperties();
        foreach (var property in properties)
        {
            // check attribute
            var cfg = property.GetCustomAttribute<ConfigureAttribute>(false);
            if (cfg == null)
                continue;
            switch (cfg.Type)
            {
                case ConfigureItemType.Text:
                    break;
                case ConfigureItemType.Integral:
                    break;
                case ConfigureItemType.Real:
                    break;
                case ConfigureItemType.Switch:
                    break;
                case ConfigureItemType.Choice:
                    break;
                case ConfigureItemType.Group:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}