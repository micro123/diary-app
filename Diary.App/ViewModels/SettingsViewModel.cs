using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Core.Configure;
using Diary.Utils;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableCollection<SettingItemModel> _settingsTree = new();

    public SettingsViewModel()
    {
        BuildTree();
    }

    private void BuildTree()
    {
        var config = App.Current.AppConfig;
        BuildTree(SettingsTree, config);
    }

    private void BuildTree(Collection<SettingItemModel> tree, object o)
    {
        var type = o.GetType();
        var properties = type.GetProperties();
        foreach (var property in properties)
        {
            // check attribute
            var cfg = property.GetCustomAttribute<ConfigureAttribute>(false);
            if (cfg == null)
                continue;
            
            SettingItemModel? item = null;
            switch(cfg)
            {
                case ConfigureGroupAttribute g:
                    var group = new SettingGroup(g.Caption);
                    BuildTree(group.Children, property.GetValue(o)!);
                    item = group;
                    break;
                case ConfigureTextAttribute t:
                    item = new SettingText(t.Caption, t.IsPassword, o, property);
                    break;
                case ConfigureIntegralAttribute i:
                    item = new SettingInteger(i.Caption, i.Min, i.Max, o, property);
                    break;
                case ConfigureRealAttribute r:
                    item = new SettingReal(r.Caption, r.Min, r.Max, o, property);
                    break;
                case ConfigureSwitchAttribute s:
                    item = new SettingSwitch(s.Caption, s.OnValue, s.OffValue, o, property);
                    break;
                case ConfigureChoiceAttribute c:
                    item = new SettingChoice(c.Caption, c.Choices, o, property);
                    break;
                default: break;
            }
            if (item != null)
                tree.Add(item);
        }
    }

    [RelayCommand]
    private void Save()
    {
        foreach (var item in SettingsTree)
        {
            item.Save();
        }
    }

    [RelayCommand]
    private void Load()
    {
        foreach (var item in SettingsTree)
        {
            item.Load();
        }
    }
}