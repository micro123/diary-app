using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FastTreeDataGrid.Engine.Infrastructure;

namespace Diary.App.Models;

public sealed class StatisticsTimeNode: IFastTreeDataGridValueProvider, IFastTreeDataGridGroup, INotifyPropertyChanged
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public double Time { get; set; }
    public double Percent { get; set; }
    public ICollection<StatisticsTimeNode> Children { get; set; } = Array.Empty<StatisticsTimeNode>();
    public object? GetValue(object? _, string key)
    {
        return key switch
        {
            nameof(Id) => Id,
            nameof(Name) => Name,
            nameof(Time) => Time,
            nameof(Percent) => Percent,
            nameof(Children) => Children,
            _ => null
        };
    }
    
    public event EventHandler<ValueInvalidatedEventArgs>? ValueInvalidated;
    public bool IsGroup => Children.Count > 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        ValueInvalidated?.Invoke(this, new ValueInvalidatedEventArgs(this, propertyName));
    }
    
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}