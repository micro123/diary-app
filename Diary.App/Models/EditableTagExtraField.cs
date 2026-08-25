using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.Data.Base;

namespace Diary.App.Models;

public partial class EditableTagExtraField : ObservableObject
{
    private readonly TagExtraFieldDefinition _definition;
    private readonly bool _isNew;
    private string _defaultValue = string.Empty;
    private EditableWorkItemExtraField _defaultValueEditor = null!;

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
        DefaultValue = string.Empty;
        Enabled = true;
        RebuildDefaultValueEditor();
    }

    public EditableTagExtraField(TagExtraFieldDefinition definition)
    {
        _definition = definition;
        FieldKey = definition.FieldKey;
        Label = definition.Label;
        Description = definition.Description;
        OptionsText = string.Join(Environment.NewLine, definition.Options);
        Type = definition.Type;
        DefaultValue = definition.DefaultValue;
        SortOrder = definition.SortOrder;
        Enabled = definition.Enabled;
        RebuildDefaultValueEditor();
    }

    public string FieldId => _definition.FieldId;
    public int TagId => _definition.TagId;
    public bool IsNew => _isNew;
    public bool CanEditType => IsNew;
    public bool IsChoice => Type == TagExtraFieldType.Choice;
    public IReadOnlyList<TagExtraFieldType> AvailableTypes { get; } =
        Enum.GetValues<TagExtraFieldType>();
    public EditableWorkItemExtraField DefaultValueEditor => _defaultValueEditor;

    public string DefaultValue
    {
        get => _defaultValue;
        set
        {
            var normalized = value ?? string.Empty;
            if (!SetProperty(ref _defaultValue, normalized))
                return;
            if (_defaultValueEditor is not null
                && !string.Equals(_defaultValueEditor.Value, normalized, StringComparison.Ordinal))
            {
                _defaultValueEditor.Value = normalized;
            }
        }
    }

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
            DefaultValue = DefaultValue,
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
        DefaultValue = source.DefaultValue;
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

        var options = GetOptions();
        if (Type == TagExtraFieldType.Choice && options.Length == 0)
        {
            error = "选项字段至少需要配置一个选项。";
            return false;
        }

        if (!TagExtraFieldValueValidator.TryValidate(
                Type,
                DefaultValue.Trim(),
                options,
                out var defaultValueError))
        {
            error = $"默认值：{defaultValueError}";
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
            DefaultValue = DefaultValue.Trim(),
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
        _definition.DefaultValue = candidate.DefaultValue;
        _definition.Enabled = candidate.Enabled;
        FieldKey = candidate.FieldKey;
        Label = candidate.Label;
        Description = candidate.Description;
        OptionsText = string.Join(Environment.NewLine, candidate.Options);
        DefaultValue = candidate.DefaultValue;
        return true;
    }

    partial void OnTypeChanged(TagExtraFieldType oldValue, TagExtraFieldType newValue)
    {
        if (_defaultValueEditor is not null && oldValue != newValue)
            DefaultValue = string.Empty;
        RebuildDefaultValueEditor();
    }

    partial void OnOptionsTextChanged(string value) => RebuildDefaultValueEditor();

    private void RebuildDefaultValueEditor()
    {
        if (_defaultValueEditor is not null)
            _defaultValueEditor.PropertyChanged -= OnDefaultValueEditorPropertyChanged;
        _defaultValueEditor = new EditableWorkItemExtraField(new WorkItemExtraField
        {
            FieldId = FieldId,
            FieldKey = FieldKey,
            Label = Label,
            Type = Type,
            Options = GetOptions(),
            Enabled = true,
            Value = DefaultValue,
        });
        _defaultValueEditor.PropertyChanged += OnDefaultValueEditorPropertyChanged;
        OnPropertyChanged(nameof(DefaultValueEditor));
    }

    private void OnDefaultValueEditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditableWorkItemExtraField.Value)
            && sender is EditableWorkItemExtraField editor)
        {
            DefaultValue = editor.Value;
        }
    }

    private string[] GetOptions() => OptionsText
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(option => option.Trim())
        .Where(option => option.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
