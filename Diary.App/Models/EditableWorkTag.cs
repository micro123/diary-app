using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Core.Data.Base;
using Diary.Database;

namespace Diary.App.Models;

public partial class EditableWorkTag : ObservableObject
{
    private readonly WorkTag _tag;
    private readonly DbInterfaceBase _database;

    public EditableWorkTag(WorkTag tag, DbInterfaceBase? database = null)
    {
        _tag = tag;
        _database = database ?? App.Instance.UseDb
            ?? throw new InvalidOperationException("数据库尚未连接。");
        Name = tag.Name;
        Color = tag.Color;
        Primary = tag.Level == TagLevels.Primary;
        Disabled = tag.Disabled;
        foreach (var (key, value) in tag.Metadata)
            Metadata.Add(CreateMetadata(key, value));
        foreach (var field in _database.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true))
            ExtraFields.Add(new EditableTagExtraField(field));
    }

    public WorkTag Tag => _tag;
    public int Id => _tag.Id;
    [ObservableProperty] private string _name;
    [ObservableProperty] private int _color;
    [ObservableProperty] private bool _primary;
    [ObservableProperty] private bool _disabled;
    public ObservableCollection<EditableWorkTagMetadata> Metadata { get; } = new();
    public ObservableCollection<EditableTagExtraField> ExtraFields { get; } = new();

    [RelayCommand]
    private void AddMetadata() => Metadata.Add(CreateMetadata(string.Empty, string.Empty));

    [RelayCommand]
    private void AddExtraField() => ExtraFields.Add(new EditableTagExtraField(Id));

    private EditableWorkTagMetadata CreateMetadata(string key, string value) =>
        new(key, value, RemoveMetadata);

    private void RemoveMetadata(EditableWorkTagMetadata item) => Metadata.Remove(item);

    public bool ApplyChanges(out string? error)
    {
        error = null;
        if (!TryBuildMetadata(out var metadata, out error))
            return false;

        var name = Name.Trim();
        if (name.Length == 0)
        {
            error = "标签名称不能为空。";
            return false;
        }

        var metadataChanged = !_tag.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal)
            .SequenceEqual(metadata.OrderBy(item => item.Key, StringComparer.Ordinal));
        var changed = !string.Equals(name, _tag.Name, StringComparison.Ordinal)
            || Color != _tag.Color
            || Primary != (_tag.Level == TagLevels.Primary)
            || Disabled != _tag.Disabled
            || metadataChanged;
        if (changed)
        {
            var candidate = _tag with
            {
                Name = name,
                Color = Color,
                Level = Primary ? TagLevels.Primary : TagLevels.Secondary,
                Disabled = Disabled,
                Metadata = metadata,
            };
            if (!_database.UpdateWorkTag(candidate))
            {
                error = "保存标签失败，名称可能与现有标签重复。";
                return false;
            }

            _tag.Name = candidate.Name;
            _tag.Color = candidate.Color;
            _tag.Level = candidate.Level;
            _tag.Disabled = candidate.Disabled;
            _tag.Metadata = candidate.Metadata;
            Name = candidate.Name;
        }

        var fieldsChanged = false;
        foreach (var field in ExtraFields)
        {
            if (!field.ApplyChanges(out error))
                return false;
            fieldsChanged = true;
        }
        return changed || fieldsChanged;
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

    public bool Delete() => _database.DeleteWorkTag(_tag);
}
