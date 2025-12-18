using System.Diagnostics;
using System.Reflection;
using Diary.App.Models;
using Diary.Core.Configure;

namespace Diary.App.Utils;

public static class SettingTreeBuilder
{
    public static void BuildTree(SettingGroup tree, object o)
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
                    var group = new SettingGroup(g.Caption, g.HelpTip);
                    BuildTree(group, property.GetValue(o)!);
                    item = group;
                    break;
                case ConfigureTextAttribute t:
                    item = new SettingText(t.Caption, t.HelpTip, t.IsPassword, o, property);
                    break;
                case ConfigureIntegralAttribute i:
                    item = new SettingInteger(i.Caption, i.HelpTip, i.Min, i.Max, o, property);
                    break;
                case ConfigureRealAttribute r:
                    item = new SettingReal(r.Caption, r.HelpTip, r.Min, r.Max, o, property);
                    break;
                case ConfigureSwitchAttribute s:
                    item = new SettingSwitch(s.Caption, s.HelpTip, o, property);
                    break;
                case ConfigureChoiceAttribute c:
                    item = new SettingChoice(c.Caption, c.HelpTip, c.Choices, o, property);
                    break;
                case ConfigureUserAttribute u:
                    item = App.Current.CreateFor(u.Caption, u.HelpTip, u.Key, o, property);
                    break;
                case ConfigurePathAttribute p:
                    item = new SettingPath(p.Caption, p.HelpTip, p.IsFolder, o, property);
                    break;
                case ConfigureButtonAttribute b:
                    item = new SettingButton(b.Caption, b.HelpTip, b.Text, b.Command);
                    break;
                default:
                    Debug.Fail($"Unknown property {property.Name}");
                    break;
            }

            tree.Children.Add(item);
        }
    }
}