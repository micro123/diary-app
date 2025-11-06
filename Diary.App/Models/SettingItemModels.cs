using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Diary.App.Models;

public abstract partial class SettingItemModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    protected virtual void LoadAction()
    {
    }

    protected virtual void SaveAction()
    {
    }

    public void Load()
    {
        LoadAction();
    }

    public void Save()
    {
        SaveAction();
    }
}

public sealed partial class SettingGroup(string title) : SettingItemModel(title)
{
    [ObservableProperty] private ObservableCollection<SettingItemModel> _children = new();

    protected override void LoadAction()
    {
        foreach (var child in Children)
        {
            child.Load();
        }
    }

    protected override void SaveAction()
    {
        foreach (var child in Children)
        {
            child.Save();
        }
    }
}

public class EditableItemModel(string title, object o, MemberInfo p) : SettingItemModel(title)
{
    protected readonly object Obj = o;
    protected readonly MemberInfo Prop = p;
}

public sealed partial class SettingText(string title, bool password, object o, MemberInfo p)
    : EditableItemModel(title, o, p)
{
    private readonly object _o = o;
    private readonly MemberInfo _p = p;

    [ObservableProperty] private string _value = "";
    public bool IsPassword { get; } = password;

    // TODO: save and load
}

public sealed partial class SettingInteger(string title, long min, long max, object o, MemberInfo p)
    : EditableItemModel(title, o, p)
{
    [ObservableProperty] private long _value = 0;

    public long MinValue { get; } = min;
    public long MaxValue { get; } = max;

    // TODO: save and load
}

public sealed partial class SettingReal(string title, double min, double max, object o, MemberInfo p)
    : EditableItemModel(title, o, p)
{
    [ObservableProperty] private double _value = 0;

    public double MinValue { get; } = min;
    public double MaxValue { get; } = max;

    // TODO: save and load
}

public sealed partial class SettingSwitch(string title, string onValue, string offValue, object o, MemberInfo p)
    : EditableItemModel(title, o, p)
{
    private readonly string _onValue = onValue;
    private readonly string _offValue = offValue;
    [ObservableProperty] private bool _value = false;


    // TODO: save and load
}

public sealed partial class SettingChoice(string title, IEnumerable<string> options, object o, MemberInfo p)
    : EditableItemModel(title, o, p)
{
    [ObservableProperty] private int _selectedIndex = 0;
    [ObservableProperty] private ObservableCollection<string> _options = new(options);

    // TODO: save and load
}