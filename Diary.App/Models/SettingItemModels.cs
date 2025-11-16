using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;

namespace Diary.App.Models;

public abstract class SettingItemModel(string title) : ObservableObject
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

public class EditableItemModel : SettingItemModel
{
    protected readonly object Obj;
    protected readonly PropertyInfo Prop;

    public EditableItemModel(string title, object o, PropertyInfo p, Func<Type,bool> check) : base(title)
    {
        Obj = o;
        Prop = p;
        if (!check(Prop.PropertyType))
        {
            throw new ArgumentException($"not a expected type: {Prop.PropertyType}");
        }
    }

    protected static bool EnsureString(Type type)
    {
        return type == typeof(string);
    }

    protected static bool EnsureBoolean(Type type)
    {
        return type == typeof(bool);
    }
    
    protected static bool EnsureInteger(Type type)
    {
        var code = Type.GetTypeCode(type);
        return code is >= TypeCode.SByte and <= TypeCode.UInt64;
    }
    
    protected static bool EnsureFloatOrDouble(Type type)
    {
        var code = Type.GetTypeCode(type);
        return code is TypeCode.Single or TypeCode.Double;
    }
}

public sealed partial class SettingText(string title, bool password, object o, PropertyInfo p)
    : EditableItemModel(title, o, p, EnsureString)
{
    private readonly object _o = o;
    private readonly PropertyInfo _p = p;

    [ObservableProperty] private string _value = "";
    public bool Password { get; } = password;
    public char MaskChar { get; } = password ? '*' : '\0';

    // TODO: save and load
    protected override void LoadAction()
    {
        Value = (string)Prop.GetValue(Obj)!;
    }

    protected override void SaveAction()
    {
        Prop.SetValue(Obj, Value);
    }
}

public sealed partial class SettingInteger(string title, long min, long max, object o, PropertyInfo p)
    : EditableItemModel(title, o, p, EnsureInteger)
{
    [ObservableProperty] private long _value;

    public long MinValue { get; } = min;
    public long MaxValue { get; } = max;

    // TODO: save and load
    protected override void LoadAction()
    {
        Value = (long)Prop.GetValue(Obj)!;
    }

    protected override void SaveAction()
    {
        Prop.SetValue(Obj, Value);
    }
}

public sealed partial class SettingReal(string title, double min, double max, object o, PropertyInfo p)
    : EditableItemModel(title, o, p, EnsureFloatOrDouble)
{
    [ObservableProperty] private double _value;

    public double MinValue { get; } = min;
    public double MaxValue { get; } = max;

    // TODO: save and load
    protected override void LoadAction()
    {
        Value = (double)Prop.GetValue(Obj)!;
    }

    protected override void SaveAction()
    {
        Prop.SetValue(Obj, Value);
    }
}

public sealed partial class SettingSwitch(string title, object o, PropertyInfo p)
    : EditableItemModel(title, o, p, EnsureBoolean)
{
    [ObservableProperty] private bool _value;
    
    // TODO: save and load
    protected override void LoadAction()
    {
        Value = (bool)Prop.GetValue(Obj)!;
    }

    protected override void SaveAction()
    {
        Prop.SetValue(Obj, Value);
    }
}

public sealed partial class SettingChoice(string title, IEnumerable<string> options, object o, PropertyInfo p)
    : EditableItemModel(title, o, p, EnsureString)
{
    [ObservableProperty] private int _selectedIndex;
    public ObservableCollection<string> Options { get; } = [..options];

    // TODO: save and load
    protected override void LoadAction()
    {
        var value = Prop.GetValue(Obj) as string;
        SelectedIndex = Options.IndexOf(value!);
    }

    protected override void SaveAction()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Options.Count)
            Prop.SetValue(Obj, Options[SelectedIndex]);
    }
}

public sealed partial class SettingButton(string title, string text, string command) : SettingItemModel(title)
{
    [ObservableProperty] private string _text = text;
    [RelayCommand]
    void Execute()
    {
        WeakReferenceMessenger.Default.Send(new RunCommandEvent(command));
    }
}
