using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.Data.Base;

namespace Diary.App.Models;

public partial class EditableTagExtraField : ObservableObject
{
    private readonly TagExtraFieldDefinition _definition;
    private readonly bool _isNew;

    public EditableTagExtraField(int tagId)
    {
        _definition = new TagExtraFieldDefinition
        {
            FieldId = Guid.NewGuid().ToString("D"),
            TagId = tagId,
        };
        _isNew = true;
        FieldKey = string.Empty;
        Label = string.Empty;
        Description = string.Empty;
        OptionsText = string.Empty;
        Type = TagExtraFieldType.Text;
        Enabled = true;
    }

    public EditableTagExtraField(TagExtraFieldDefinition definition)
    {
        _definition = definition;
        FieldKey = definition.FieldKey;
        Label = definition.Label;
        Description = definition.Description;
        OptionsText = string.Join(Environment.NewLine, definition.Options);
        Type = definition.Type;
        SortOrder = definition.SortOrder;
        Enabled = definition.Enabled;
    }

    public string FieldId => _definition.FieldId;
    public int TagId => _definition.TagId;
    public bool IsNew => _isNew;
    public bool CanEditType => IsNew;
    public bool IsChoice => Type == TagExtraFieldType.Choice;
    public IReadOnlyList<TagExtraFieldType> AvailableTypes { get; } =
        Enum.GetValues<TagExtraFieldType>();

    [ObservableProperty] private string _fieldKey;
    [ObservableProperty] private string _label;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoice))]
    private TagExtraFieldType _type;
    [ObservableProperty] private string _description;
    [ObservableProperty] private int _sortOrder;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _optionsText;

    public EditableTagExtraField Clone()
    {
        var clone = new EditableTagExtraField(new TagExtraFieldDefinition
        {
            FieldId = FieldId,
            TagId = TagId,
            FieldKey = FieldKey,
            Label = Label,
            Type = Type,
            Description = Description,
            SortOrder = SortOrder,
            Options = GetOptions(),
            Enabled = Enabled,
        });
        return clone;
    }

    public void CopyFrom(EditableTagExtraField source)
    {
        FieldKey = source.FieldKey;
        Label = source.Label;
        Type = source.Type;
        Description = source.Description;
        SortOrder = source.SortOrder;
        OptionsText = source.OptionsText;
        Enabled = source.Enabled;
    }

    public bool Validate(out string? error)
    {
        error = null;
        var key = FieldKey.Trim();
        var label = Label.Trim();
        if (!TagExtraFieldKeyRules.IsValid(key))
        {
            error = $"字段标识无效：{key}。只能使用英文、数字、点、下划线和短横线。";
            return false;
        }

        if (label.Length == 0)
        {
            error = "字段名称不能为空。";
            return false;
        }

        return true;
    }

    public bool ApplyChanges(out string? error)
    {
        error = null;
        if (!Validate(out error))
            return false;

        var key = FieldKey.Trim();
        var label = Label.Trim();
        var db = App.Instance.UseDb;
        if (db is null)
        {
            error = "数据库尚未连接。";
            return false;
        }

        if (!db.IsTagExtraFieldKeyAvailable(key, IsNew ? null : FieldId))
        {
            error = $"字段标识已存在：{key}";
            return false;
        }

        var candidate = new TagExtraFieldDefinition
        {
            FieldId = FieldId,
            FieldKey = TagExtraFieldKeyRules.Normalize(key),
            TagId = TagId,
            Label = label,
            Type = Type,
            Description = Description.Trim(),
            SortOrder = SortOrder,
            Options = GetOptions(),
            Enabled = Enabled,
        };

        var changed = IsNew
            ? db.CreateTagExtraFieldDefinition(candidate)
            : db.UpdateTagExtraFieldDefinition(candidate);
        if (!changed)
        {
            error = "保存附加字段失败。字段类型和字段标识创建后不能修改。";
            return false;
        }

        _definition.FieldKey = candidate.FieldKey;
        _definition.Label = candidate.Label;
        _definition.Type = candidate.Type;
        _definition.Description = candidate.Description;
        _definition.SortOrder = candidate.SortOrder;
        _definition.Options = candidate.Options;
        _definition.Enabled = candidate.Enabled;
        FieldKey = candidate.FieldKey;
        Label = candidate.Label;
        Description = candidate.Description;
        OptionsText = string.Join(Environment.NewLine, candidate.Options);
        return true;
    }

    private string[] GetOptions() => OptionsText
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(option => option.Trim())
        .Where(option => option.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
