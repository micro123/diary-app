using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Core.Data.Base;

namespace Diary.App.Models;

public partial class EditableWorkTag : ObservableObject
{
    private readonly WorkTag _tag;

    public EditableWorkTag(WorkTag tag)
    {
        _tag = tag;
        Name = tag.Name;
        Color = tag.Color;
        Primary = tag.Level == TagLevels.Primary;
        Disabled = tag.Disabled;
        foreach (var (key, value) in tag.Metadata)
            Metadata.Add(CreateMetadata(key, value));
    }

    public WorkTag Tag => _tag;
    public int Id => _tag.Id;
    [ObservableProperty] private string _name;
    [ObservableProperty] private int _color;
    [ObservableProperty] private bool _primary;
    [ObservableProperty] private bool _disabled;
    public ObservableCollection<EditableWorkTagMetadata> Metadata { get; } = new();

    [RelayCommand]
    private void AddMetadata() => Metadata.Add(CreateMetadata(string.Empty, string.Empty));

    private EditableWorkTagMetadata CreateMetadata(string key, string value) =>
        new(key, value, RemoveMetadata);

    private void RemoveMetadata(EditableWorkTagMetadata item) => Metadata.Remove(item);

    public bool ApplyChanges(out string? error)
    {
        error = null;
        if (!TryBuildMetadata(out var metadata, out error))
            return false;

        var metadataChanged = !_tag.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal)
            .SequenceEqual(metadata.OrderBy(item => item.Key, StringComparer.Ordinal));
        var changed = Color != _tag.Color
            || Primary != (_tag.Level == TagLevels.Primary)
            || Disabled != _tag.Disabled
            || metadataChanged;
        if (!changed)
            return false;

        _tag.Color = Color;
        _tag.Level = Primary ? TagLevels.Primary : TagLevels.Secondary;
        _tag.Disabled = Disabled;
        _tag.Metadata = metadata;
        App.Instance.UseDb!.UpdateWorkTag(_tag);
        return true;
    }

    private bool TryBuildMetadata(
        out Dictionary<string, string> metadata,
        out string? error)
    {
        metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        foreach (var item in Metadata)
        {
            var key = item.Key.Trim();
            if (key.Length == 0)
            {
                if (string.IsNullOrWhiteSpace(item.Value))
                    continue;
                error = "标签元数据的键不能为空。";
                return false;
            }

            if (!metadata.TryAdd(key, item.Value))
            {
                error = $"标签元数据键重复：{key}";
                return false;
            }
        }

        return true;
    }

    public bool Delete() => App.Instance.UseDb!.DeleteWorkTag(_tag);
}
