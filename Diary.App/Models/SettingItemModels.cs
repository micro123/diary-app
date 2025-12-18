using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Ursa.Controls;

namespace Diary.App.Models;

public abstract class SettingItemModel(string title, string helpTip) : ObservableObject
{
    public string Title { get; } = title;
    public string HelpTip { get; } = helpTip;
    public bool   HasHelp => !string.IsNullOrWhiteSpace(HelpTip);

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

public sealed partial class SettingGroup(string title, string helpTip = "") : SettingItemModel(title, helpTip)
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

    protected EditableItemModel(string title, string helpTip, object o, PropertyInfo p, Func<Type,bool> check) : base(title, helpTip)
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

public sealed partial class SettingText(string title, string helpTip, bool password, object o, PropertyInfo p)
    : EditableItemModel(title, helpTip, o, p, EnsureString)
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

public sealed partial class SettingPath(string title, string helpTip, bool isFolder, object o, PropertyInfo p)
    : EditableItemModel(title, helpTip, o, p, EnsureString)
{
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _dirName = "";
    public string PickerTitle => isFolder ? "选择目录" : "选择文件";

    public UsePickerTypes PickerType => isFolder ? UsePickerTypes.OpenFolder : UsePickerTypes.OpenFile;
    
    protected override void LoadAction()
    {
        Value = (string)Prop.GetValue(Obj)!;
        DirName = Path.GetDirectoryName(Value) ?? "";
    }

    protected override void SaveAction()
    {
        Prop.SetValue(Obj, Value);
    }
}

public sealed partial class SettingInteger(string title, string helpTip, long min, long max, object o, PropertyInfo p)
    : EditableItemModel(title, helpTip, o, p, EnsureInteger)
{
    [ObservableProperty] private long _value;

    public long MinValue { get; } = min;
    public long MaxValue { get; } = max;

    protected override void LoadAction()
    {
        Value = Convert.ToInt64(Prop.GetValue(Obj)!);
    }

    protected override void SaveAction()
    {
        object? value = null;
        try
        {
            switch (Type.GetTypeCode(Prop.PropertyType))
            {
                case TypeCode.SByte: value = Convert.ToSByte(Value); break;
                case TypeCode.Byte: value = Convert.ToByte(Value); break;
                case TypeCode.Int16: value = Convert.ToInt16(Value); break;
                case TypeCode.UInt16: value = Convert.ToUInt16(Value); break;
                case TypeCode.Int32: value = Convert.ToInt32(Value); break;
                case TypeCode.UInt32: value = Convert.ToUInt32(Value); break;
                case TypeCode.Int64: value = Convert.ToInt64(Value); break;
                case TypeCode.UInt64: value = Convert.ToUInt64(Value); break;
            }
        }
        catch (Exception)
        {
            value = null;
        }
        if (value != null)
            Prop.SetValue(Obj, value);
    }
}

public sealed partial class SettingReal(string title, string helpTip, double min, double max, object o, PropertyInfo p)
    : EditableItemModel(title, helpTip, o, p, EnsureFloatOrDouble)
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

public sealed partial class SettingSwitch(string title, string helpTip, object o, PropertyInfo p)
    : EditableItemModel(title, helpTip, o, p, EnsureBoolean)
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

public sealed partial class SettingChoice(string title, string helpTip, IEnumerable<string> options, object o, PropertyInfo p)
    : EditableItemModel(title, helpTip, o, p, EnsureString)
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

public sealed partial class SettingButton(string title, string helpTip, string text, string command) : SettingItemModel(title, helpTip)
{
    [ObservableProperty] private string _text = text;
    [RelayCommand]
    void Execute()
    {
        WeakReferenceMessenger.Default.Send(new RunCommandEvent(command));
    }
}
