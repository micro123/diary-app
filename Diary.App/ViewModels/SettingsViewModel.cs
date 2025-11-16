using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.Core.Configure;
using Diary.Utils;
using Ursa.Controls;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private SettingGroup _settingsTree = new("Root");

    public SettingsViewModel()
    {
        BuildTree();
    }

    private void BuildTree()
    {
        var config = App.Current.AppConfig;
        BuildTree(SettingsTree.Children, config);
    }

    private void BuildTree(Collection<SettingItemModel> tree, object o)
    {
        var type = o.GetType();
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var property in properties)
        {
            // check attribute
            var cfg = property.GetCustomAttribute<ConfigureAttribute>(false);
            if (cfg == null)
                continue;

            SettingItemModel? item = null;
            switch (cfg)
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
                    item = new SettingSwitch(s.Caption, o, property);
                    break;
                case ConfigureChoiceAttribute c:
                    item = new SettingChoice(c.Caption, c.Choices, o, property);
                    break;
                case ConfigureUserAttribute u:
                    item = App.Current.CreateFor(u.Caption, u.Key, o, property);
                    break;
                case ConfigureButtonAttribute b:
                    item = new SettingButton(b.Caption, b.Text, b.Command);
                    break;
                default:
                    Debug.Fail($"Unknown property {property.Name}");
                    break;
            }

            tree.Add(item);
        }
    }

    [RelayCommand]
    private void Save()
    {
        SettingsTree.Save();
        NotificationManager?.Show("已保存", NotificationType.Success);
        Messenger.Send(new ConfigUpdateEvent());
    }

    [RelayCommand]
    private async Task Load()
    {
        var confirm = await MessageBox.ShowOverlayAsync(
            message: "所做的所有更改均被丢弃",
            title: "确认执行吗？",
            icon: MessageBoxIcon.Warning,
            button: MessageBoxButton.OKCancel
        );
        Debug.WriteLine($"Result: {confirm}");
        if (confirm != MessageBoxResult.OK)
            return;

        ForceLoad();

        NotificationManager?.Show("更改已丢弃!", NotificationType.Information);
    }

    [RelayCommand]
    private void ForceLoad()
    {
        SettingsTree.Load();
    }
}