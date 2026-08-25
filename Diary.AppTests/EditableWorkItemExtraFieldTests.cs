using Diary.App.Models;
using Diary.Core.Data.Base;

namespace Diary.AppTests;

[TestClass]
public sealed class EditableWorkItemExtraFieldTests
{
    [TestMethod]
    [DataRow(TagExtraFieldType.Text, "text")]
    [DataRow(TagExtraFieldType.MultilineText, "multiline")]
    [DataRow(TagExtraFieldType.Integer, "integer")]
    [DataRow(TagExtraFieldType.Decimal, "decimal")]
    [DataRow(TagExtraFieldType.Boolean, "boolean")]
    [DataRow(TagExtraFieldType.Date, "date")]
    [DataRow(TagExtraFieldType.Time, "time")]
    [DataRow(TagExtraFieldType.DateTime, "datetime")]
    [DataRow(TagExtraFieldType.Choice, "choice")]
    public void FieldTypeSelectsExactlyOneEditor(TagExtraFieldType type, string expected)
    {
        var field = Create(type);
        var editors = new Dictionary<string, bool>
        {
            ["text"] = field.UsesTextEditor,
            ["multiline"] = field.UsesMultilineTextEditor,
            ["integer"] = field.UsesIntegerEditor,
            ["decimal"] = field.UsesDecimalEditor,
            ["boolean"] = field.UsesBooleanEditor,
            ["date"] = field.UsesDateEditor,
            ["time"] = field.UsesTimeEditor,
            ["datetime"] = field.UsesDateTimeEditor,
            ["choice"] = field.UsesChoiceEditor,
        };

        Assert.AreEqual(expected, editors.Single(editor => editor.Value).Key);
    }

    [TestMethod]
    public void TypedEditorsKeepCanonicalStringValues()
    {
        var integer = Create(TagExtraFieldType.Integer, "12");
        Assert.AreEqual(12m, integer.NumericValue);
        integer.NumericValue = -7m;
        Assert.AreEqual("-7", integer.Value);

        var decimalField = Create(TagExtraFieldType.Decimal);
        decimalField.NumericValue = 12.5m;
        Assert.AreEqual("12.5", decimalField.Value);

        var boolean = Create(TagExtraFieldType.Boolean);
        Assert.IsNull(boolean.BooleanValue);
        boolean.BooleanValue = false;
        Assert.AreEqual("False", boolean.Value);
        Assert.AreEqual("否", boolean.BooleanDisplay);

        var date = Create(TagExtraFieldType.Date);
        date.DateValue = new DateTime(2026, 8, 21);
        Assert.AreEqual("2026-08-21", date.Value);

        var time = Create(TagExtraFieldType.Time);
        time.TimeValue = new TimeSpan(9, 30, 0);
        Assert.AreEqual("09:30:00", time.Value);
    }

    [TestMethod]
    public void DateTimeEditorPreservesOffsetWhenChangingParts()
    {
        var field = Create(TagExtraFieldType.DateTime, "2026-08-21T14:05:06+08:00");

        field.DateTimeTimeValue = new TimeSpan(15, 30, 0);

        Assert.AreEqual("2026-08-21T15:30:00.0000000+08:00", field.Value);
        Assert.AreEqual(new DateTime(2026, 8, 21), field.DateTimeDateValue);
        Assert.AreEqual(new TimeSpan(15, 30, 0), field.DateTimeTimeValue);
        Assert.IsTrue(TagExtraFieldValueValidator.TryValidate(
            TagExtraFieldType.DateTime,
            field.Value,
            [],
            out _));
    }

    [TestMethod]
    public void ChoiceEditorSupportsSelectionAndClearing()
    {
        var field = Create(TagExtraFieldType.Choice, options: ["开发", "测试"]);

        field.SelectedChoice = "测试";
        Assert.AreEqual("测试", field.Value);

        field.ClearValueCommand.Execute(null);
        Assert.AreEqual(string.Empty, field.Value);
        Assert.IsNull(field.SelectedChoice);
    }

    [TestMethod]
    public void ReadOnlyFieldCannotExecuteClearCommand()
    {
        var field = Create(TagExtraFieldType.Choice, "开发", ["开发"], isReadOnly: true);

        Assert.IsFalse(field.ClearValueCommand.CanExecute(null));
        Assert.AreEqual("开发", field.Value);
    }

    [TestMethod]
    public void DisabledFieldIsReadOnlyAndKeepsValue()
    {
        var field = Create(TagExtraFieldType.Text, "历史值", enabled: false);

        Assert.IsFalse(field.Enabled);
        Assert.IsTrue(field.IsDisabled);
        Assert.IsTrue(field.IsReadOnly);
        Assert.IsFalse(field.ClearValueCommand.CanExecute(null));
        Assert.AreEqual("历史值", field.Value);
    }

    [TestMethod]
    public void TagDefinitionDefaultUsesTypedEditorAndCanonicalValue()
    {
        var definition = new EditableTagExtraField(new TagExtraFieldDefinition
        {
            FieldId = "definition-id",
            FieldKey = "default.integer",
            TagId = 1,
            Label = "整数默认值",
            Type = TagExtraFieldType.Integer,
            DefaultValue = "12",
        });

        Assert.AreEqual(12m, definition.DefaultValueEditor.NumericValue);
        definition.DefaultValueEditor.NumericValue = 25m;

        Assert.AreEqual("25", definition.DefaultValue);
        Assert.IsTrue(definition.Validate(out var error), error);
    }

    [TestMethod]
    public void TagDefinitionChoiceDefaultMustExistInOptions()
    {
        var definition = new EditableTagExtraField(new TagExtraFieldDefinition
        {
            FieldId = "choice-definition-id",
            FieldKey = "default.choice",
            TagId = 1,
            Label = "选项默认值",
            Type = TagExtraFieldType.Choice,
            Options = ["开发", "测试"],
            DefaultValue = "不存在",
        });

        Assert.IsFalse(definition.Validate(out var error));
        StringAssert.Contains(error, "必须选择已配置的选项");

        definition.DefaultValueEditor.SelectedChoice = "测试";
        Assert.AreEqual("测试", definition.DefaultValue);
        Assert.IsTrue(definition.Validate(out error), error);
    }

    private static EditableWorkItemExtraField Create(
        TagExtraFieldType type,
        string value = "",
        IReadOnlyList<string>? options = null,
        bool isReadOnly = false,
        bool enabled = true) =>
        new(new WorkItemExtraField
        {
            FieldId = "field-id",
            FieldKey = "test.field",
            TagId = 1,
            TagName = "测试标签",
            Label = "测试字段",
            Type = type,
            Options = options ?? [],
            Enabled = enabled,
            Value = value,
        }, isReadOnly);
}
