using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Diary.App.Models;

public abstract class SettingItemModel(string title) : ObservableObject
{
    public string Title { get; init; } = title;
    protected virtual Action? LoadAction { get; } = null;
    protected virtual Action? SaveAction { get; } = null;

    public void Load()
    {
        LoadAction?.Invoke();
    }

    public void Save()
    {
        SaveAction?.Invoke();
    }
}

public sealed class SettingGroup(string title) : SettingItemModel(title)
{
    
}
