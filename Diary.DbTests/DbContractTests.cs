using Diary.Core;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Jira;
using Diary.RedMine;

namespace Diary.DbTests;

/// <summary>
/// DB 契约测试：所有 provider（SQLite/PostgreSQL）共用的场景。
/// 派生类实现 <see cref="CreateDb"/> 返回 connected+Initialized+数据空的库。
/// </summary>
[TestClass]
public abstract class DbContractTests
{
    protected abstract DbInterfaceBase CreateDb(Func<uint, Migration?>? getMigration = null);
    protected abstract Migration? GetProductionMigration(uint version);

    protected static IRedMineDb GetRedMine(DbInterfaceBase db, string instanceId = "redmine.default")
        => db.GetExtension<IRedMineDb>(instanceId, new RedMinePlugin().GetMigrations())!;

    protected static IJiraDb GetJira(DbInterfaceBase db, string instanceId = "jira.default")
        => db.GetExtension<IJiraDb>(instanceId, new JiraPlugin().GetMigrations())!;

    [TestMethod]
    public void JiraIssues_IntegerFlagsRoundTripAndOpenFilter()
    {
        using var db = CreateDb();
        var jira = GetJira(db);
        jira.UpsertProject(new JiraProject("ARCH", "Archived", "Historical", true));
        jira.UpsertIssue(new JiraIssue("CDP-1", "Open issue", "CDP", "CDP", "Doing", false));
        jira.UpsertIssue(new JiraIssue("CDP-2", "Closed issue", "CDP", "CDP", "Done", true));

        var openIssues = jira.GetIssues();
        var allIssues = jira.GetIssues(openOnly: false);

        Assert.AreEqual(1, openIssues.Count);
        Assert.AreEqual("CDP-1", openIssues.Single().Key);
        Assert.AreEqual(2, allIssues.Count);
        Assert.IsTrue(allIssues.Single(issue => issue.Key == "CDP-2").Disabled);
        Assert.IsTrue(jira.GetProjects().Single(project => project.Key == "ARCH").Archived);
    }

    // ---------- WorkTag ----------

    [TestMethod]
    public void CreateWorkTag_Primary_MapsFields()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("工作", true, 0x112233,
            new Dictionary<string, string> { ["projectNumber"] = "PRJ-2026-001" });
        Assert.IsTrue(tag.Id > 0);
        Assert.AreEqual("工作", tag.Name);
        Assert.AreEqual(0x112233, tag.Color);
        Assert.AreEqual(TagLevels.Primary, tag.Level);
        Assert.IsFalse(tag.Disabled);
        Assert.AreEqual("PRJ-2026-001", tag.Metadata["projectNumber"]);
    }

    [TestMethod]
    public void CreateWorkTag_Secondary_HasSecondaryLevel()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("次要", false, 0);
        Assert.AreEqual(TagLevels.Secondary, tag.Level);
    }

    [TestMethod]
    public void AllWorkTags_ReturnsAllOrdered()
    {
        using var db = CreateDb();
        db.CreateWorkTag("p1", true, 0);
        db.CreateWorkTag("s1", false, 0);
        var all = db.AllWorkTags().ToList();
        Assert.AreEqual(2, all.Count);
        CollectionAssert.AreEquivalent(new[] { "p1", "s1" }, all.Select(t => t.Name).ToList());
    }

    [TestMethod]
    public void UpdateWorkTag_Persists()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("t", true, 1);
        tag.Name = "renamed";
        tag.Color = 99;
        tag.Level = TagLevels.Secondary;
        tag.Disabled = true;
        tag.Metadata["customer"] = "客户 A";
        Assert.IsTrue(db.UpdateWorkTag(tag));
        var all = db.AllWorkTags();
        var got = all.Single(x => x.Id == tag.Id);
        Assert.AreEqual("renamed", got.Name);
        Assert.AreEqual(99, got.Color);
        Assert.AreEqual(TagLevels.Secondary, got.Level);
        Assert.IsTrue(got.Disabled);
        Assert.AreEqual("客户 A", got.Metadata["customer"]);
    }

    [TestMethod]
    public void UpdateWorkTag_DuplicateName_ReturnsFalseWithoutChangingTag()
    {
        using var db = CreateDb();
        var first = db.CreateWorkTag("first", true, 1);
        db.CreateWorkTag("second", false, 2);
        first.Name = "second";

        Assert.IsFalse(db.UpdateWorkTag(first));
        Assert.AreEqual("first", db.AllWorkTags().Single(tag => tag.Id == first.Id).Name);
    }

    [TestMethod]
    public void UpdateWorkTag_ZeroId_ReturnsFalse()
    {
        using var db = CreateDb();
        Assert.IsFalse(db.UpdateWorkTag(new WorkTag { Id = 0 }));
    }

    [TestMethod]
    public void DeleteWorkTag_RemovesIt()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("t", true, 0);
        Assert.IsTrue(db.DeleteWorkTag(tag));
        Assert.IsFalse(db.AllWorkTags().Any(x => x.Id == tag.Id));
    }

    /// <summary>重名（tag_name UNIQUE）时两端都应跳过插入、返回空 WorkTag（Id=0），
    /// 让 TagEditorViewModel 的"重复标签名"提示在 Pg 下也能触发（非抛异常）。</summary>
    [TestMethod]
    public void CreateWorkTag_DuplicateName_ReturnsEmpty()
    {
        using var db = CreateDb();
        var first = db.CreateWorkTag("dup", true, 0);
        Assert.IsTrue(first.Id > 0);
        var second = db.CreateWorkTag("dup", true, 0);
        Assert.AreEqual(0, second.Id);
    }

    [TestMethod]
    public void UpdateWorkTagId_ChangesId()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("t", true, 0);
        Assert.IsTrue(db.UpdateWorkTagId(tag.Id, 5000));
        Assert.IsTrue(db.AllWorkTags().Any(x => x.Id == 5000));
    }

    // ---------- 标签附加字段 ----------

    [TestMethod]
    public void TagExtraFieldDefinition_GlobalKeyAndImmutableType()
    {
        using var db = CreateDb();
        var meeting = db.CreateWorkTag("会议", true, 0);
        var overtime = db.CreateWorkTag("加班", false, 0);
        var definition = new TagExtraFieldDefinition
        {
            FieldKey = "meeting.date",
            TagId = meeting.Id,
            Label = "会议日期",
            Type = TagExtraFieldType.Date,
        };

        Assert.IsTrue(db.CreateTagExtraFieldDefinition(definition));
        Assert.IsFalse(db.IsTagExtraFieldKeyAvailable("MEETING.DATE"));
        Assert.IsFalse(db.CreateTagExtraFieldDefinition(new TagExtraFieldDefinition
        {
            FieldKey = "meeting.date",
            TagId = overtime.Id,
            Label = "重复字段",
            Type = TagExtraFieldType.Text,
        }));

        definition.Type = TagExtraFieldType.DateTime;
        Assert.IsFalse(db.UpdateTagExtraFieldDefinition(definition));
        Assert.AreEqual(TagExtraFieldType.Date,
            db.GetTagExtraFieldDefinitions(meeting.Id, includeDisabled: true).Single().Type);
    }

    [TestMethod]
    public void TagExtraFieldDefinition_DisableKeepsDefinition()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("项目", true, 0);
        var definition = new TagExtraFieldDefinition
        {
            FieldKey = "project.number",
            TagId = tag.Id,
            Label = "项目编号",
            Type = TagExtraFieldType.Text,
        };
        Assert.IsTrue(db.CreateTagExtraFieldDefinition(definition));

        definition.Enabled = false;
        Assert.IsTrue(db.UpdateTagExtraFieldDefinition(definition));
        Assert.AreEqual(0, db.GetTagExtraFieldDefinitions(tag.Id).Count);
        var disabled = db.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true).Single();
        Assert.IsFalse(disabled.Enabled);
        Assert.AreEqual("project.number", disabled.FieldKey);
    }

    [TestMethod]
    public void TagExtraFieldDefinition_DefaultValueRoundTripsAndValidatesChoice()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("默认值", true, 0);
        var definition = new TagExtraFieldDefinition
        {
            FieldKey = "default.stage",
            TagId = tag.Id,
            Label = "默认阶段",
            Type = TagExtraFieldType.Choice,
            Options = ["开发", "测试"],
            DefaultValue = "开发",
        };

        Assert.IsTrue(db.CreateTagExtraFieldDefinition(definition));
        var created = db.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true).Single();
        Assert.AreEqual("开发", created.DefaultValue);

        definition.DefaultValue = "测试";
        Assert.IsTrue(db.UpdateTagExtraFieldDefinition(definition));
        Assert.AreEqual("测试",
            db.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true).Single().DefaultValue);

        definition.DefaultValue = "不存在";
        Assert.IsFalse(db.UpdateTagExtraFieldDefinition(definition));
        Assert.AreEqual("测试",
            db.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true).Single().DefaultValue);
    }

    [TestMethod]
    public void ProductionMigration_AddsDefaultValueWithoutChangingExistingDefinitions()
    {
        using var db = CreateDb(GetProductionMigration);
        var tag = db.CreateWorkTag("旧版本字段", true, 0);
        Assert.IsTrue(db.ExecRaw(
            "INSERT INTO tag_extra_field_definitions " +
            "(field_id, field_key, tag_id, label, field_type, description, sort_order, options_json, enabled) " +
            $"VALUES ('legacy-default-field', 'legacy.default', {tag.Id}, '旧字段', 0, '', 0, '[]', TRUE);"));

        var result = db.MigrateTo(DataVersion.VersionCode, new DbMigrationOptions(CreateBackup: false));

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(DataVersion.VersionCode, db.GetDataVersion());
        var definition = db.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true).Single();
        Assert.AreEqual(string.Empty, definition.DefaultValue);
        Assert.IsTrue(result.AppliedMigrations.Contains("00010000-00010001"));
    }

    [TestMethod]
    public void WorkItemExtraFieldValues_SaveAndLoadByCurrentTags()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("会议", true, 0);
        var definition = new TagExtraFieldDefinition
        {
            FieldKey = "meeting.participants",
            TagId = tag.Id,
            Label = "参会人",
            Type = TagExtraFieldType.Text,
        };
        Assert.IsTrue(db.CreateTagExtraFieldDefinition(definition));
        var item = db.CreateWorkItem("2026-08-17", "会议记录");
        Assert.IsTrue(db.WorkItemAddTag(item, tag));

        Assert.IsTrue(db.SaveWorkItemExtraFieldValues(item.Id, [new WorkItemExtraFieldValue
        {
            WorkItemId = item.Id,
            FieldId = definition.FieldId,
            Value = "张三、李四",
        }]));
        var field = db.GetWorkItemExtraFields(item).Single();
        Assert.AreEqual("meeting.participants", field.FieldKey);
        Assert.AreEqual("张三、李四", field.Value);

        definition.Enabled = false;
        Assert.IsTrue(db.UpdateTagExtraFieldDefinition(definition));
        field = db.GetWorkItemExtraFields(item).Single();
        Assert.IsFalse(field.Enabled);
        Assert.AreEqual("张三、李四", field.Value);

        var disabledWithoutValue = new TagExtraFieldDefinition
        {
            FieldKey = "meeting.disabled-empty",
            TagId = tag.Id,
            Label = "停用空字段",
            Type = TagExtraFieldType.Text,
        };
        Assert.IsTrue(db.CreateTagExtraFieldDefinition(disabledWithoutValue));
        disabledWithoutValue.Enabled = false;
        Assert.IsTrue(db.UpdateTagExtraFieldDefinition(disabledWithoutValue));
        Assert.AreEqual(1, db.GetWorkItemExtraFields(item).Count);

        definition.Enabled = true;
        Assert.IsTrue(db.UpdateTagExtraFieldDefinition(definition));

        Assert.IsTrue(db.SaveWorkItemExtraFieldValues(item.Id, [new WorkItemExtraFieldValue
        {
            WorkItemId = item.Id,
            FieldId = definition.FieldId,
            Value = string.Empty,
        }]));
        Assert.AreEqual(string.Empty, db.GetWorkItemExtraFields(item).Single().Value);

        Assert.IsTrue(db.MarkWorkItemReadOnly(item));
        Assert.IsFalse(db.SaveWorkItemExtraFieldValues(item.Id, [new WorkItemExtraFieldValue
        {
            WorkItemId = item.Id,
            FieldId = definition.FieldId,
            Value = "只读记录不可修改",
        }]));
        Assert.AreEqual(string.Empty, db.GetWorkItemExtraFields(item).Single().Value);
    }

    [TestMethod]
    public void WorkItemExtraFieldValues_BatchLoadMatchesIndividualResults()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("批量字段", true, 0);
        var definition = new TagExtraFieldDefinition
        {
            FieldKey = "batch.value",
            TagId = tag.Id,
            Label = "批量值",
            Type = TagExtraFieldType.Text,
        };
        Assert.IsTrue(db.CreateTagExtraFieldDefinition(definition));

        var first = db.CreateWorkItem("2026-08-22", "批量一");
        var second = db.CreateWorkItem("2026-08-22", "批量二");
        var withoutTag = db.CreateWorkItem("2026-08-22", "无标签");
        Assert.IsTrue(db.WorkItemAddTag(first, tag));
        Assert.IsTrue(db.WorkItemAddTag(second, tag));
        Assert.IsTrue(db.SaveWorkItemExtraFieldValues(first.Id,
        [
            new WorkItemExtraFieldValue
            {
                WorkItemId = first.Id,
                FieldId = definition.FieldId,
                Value = "已填写",
            },
        ]));

        var batch = db.GetWorkItemExtraFieldsByWorkItemIds(
            [first.Id, second.Id, withoutTag.Id, first.Id, 0]);

        Assert.AreEqual("已填写", batch[first.Id].Single().Value);
        Assert.AreEqual(string.Empty, batch[second.Id].Single().Value);
        Assert.IsFalse(batch.ContainsKey(withoutTag.Id));
        CollectionAssert.AreEqual(
            db.GetWorkItemExtraFields(first).Select(field => field.FieldId).ToArray(),
            batch[first.Id].Select(field => field.FieldId).ToArray());
    }

    // ---------- WorkItem + WorkNote ----------

    [TestMethod]
    public void CreateWorkItem_MapsFields()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "干活");
        Assert.IsTrue(item.Id > 0);
        Assert.AreEqual("2026-08-01", item.CreateDate);
        Assert.AreEqual("干活", item.Comment);
        Assert.AreEqual(0.0, item.Time);
        Assert.AreEqual(WorkPriorities.P0, item.Priority);
        Assert.IsFalse(item.IsReadOnly);
    }

    [TestMethod]
    public void TransactionRollback_DoesNotPersistWorkItem()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.BeginTransaction());
        var item = db.CreateWorkItem("2026-08-01", "rollback");
        Assert.IsTrue(item.Id > 0);
        Assert.IsTrue(db.RollbackTransaction());

        Assert.AreEqual(0, db.GetWorkItemByDate("2026-08-01").Count);
    }

    [TestMethod]
    public void GetWorkItemByDate_ReturnsItemsForDate()
    {
        using var db = CreateDb();
        db.CreateWorkItem("2026-08-01", "a");
        db.CreateWorkItem("2026-08-01", "b");
        db.CreateWorkItem("2026-08-02", "c");
        var items = db.GetWorkItemByDate("2026-08-01").ToList();
        Assert.AreEqual(2, items.Count);
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, items.Select(i => i.Comment).ToList());
    }

    [TestMethod]
    public void GetWorkItemByDateRange_IncludesBothEnds()
    {
        using var db = CreateDb();
        db.CreateWorkItem("2026-08-01", "a");
        db.CreateWorkItem("2026-08-05", "b");
        db.CreateWorkItem("2026-08-10", "c");
        var items = db.GetWorkItemByDateRange("2026-08-01", "2026-08-05").ToList();
        Assert.AreEqual(2, items.Count);
    }

    [TestMethod]
    public void UpdateWorkItem_Persists()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        item.Comment = "changed";
        item.Time = 1.5;
        item.Priority = WorkPriorities.P3;
        Assert.IsTrue(db.UpdateWorkItem(item));
        var got = db.GetWorkItemByDate("2026-08-01").Single(i => i.Id == item.Id);
        Assert.AreEqual("changed", got.Comment);
        Assert.AreEqual(1.5, got.Time);
        Assert.AreEqual(WorkPriorities.P3, got.Priority);
    }

    [TestMethod]
    public void DeleteWorkItem_RemovesIt()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        Assert.IsTrue(db.DeleteWorkItem(item));
        Assert.IsFalse(db.GetWorkItemByDate("2026-08-01").Any(i => i.Id == item.Id));
    }

    [TestMethod]
    public void UpdateWorkItemId_ChangesId()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        Assert.IsTrue(db.UpdateWorkItemId(item.Id, 9000));
        Assert.IsTrue(db.GetWorkItemByDate("2026-08-01").Any(i => i.Id == 9000));
    }

    [TestMethod]
    public void ReadOnlyWorkItem_RejectsMutationsButAllowsDelete()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "导入记录");
        var tag = db.CreateWorkTag("导入标签", true, 0);
        Assert.IsTrue(db.WorkItemAddTag(item, tag));
        db.WorkUpdateNote(item, "导入备注");

        Assert.IsTrue(db.MarkWorkItemReadOnly(item));
        Assert.IsTrue(item.IsReadOnly);
        var changed = item with { Comment = "不应保存", Time = 4.0 };
        Assert.IsFalse(db.UpdateWorkItem(changed));
        Assert.IsFalse(db.UpdateWorkItemId(item.Id, 9001));
        Assert.ThrowsExactly<InvalidOperationException>(() => db.WorkUpdateNote(item, "不应保存"));
        Assert.ThrowsExactly<InvalidOperationException>(() => db.WorkDeleteNote(item));
        Assert.IsFalse(db.WorkItemRemoveTag(item, tag));
        Assert.IsFalse(db.WorkItemCleanTags(item));

        var reloaded = db.GetWorkItemByDate("2026-08-01").Single();
        Assert.IsTrue(reloaded.IsReadOnly);
        Assert.AreEqual("导入记录", reloaded.Comment);
        Assert.AreEqual("导入备注", db.WorkGetNote(reloaded));
        Assert.AreEqual(1, db.GetWorkItemTags(reloaded).Count);
        Assert.IsTrue(db.DeleteWorkItem(reloaded));
    }

    [TestMethod]
    public void WorkNote_UpdateGetDelete()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        Assert.IsNull(db.WorkGetNote(item));
        db.WorkUpdateNote(item, "first note");
        Assert.AreEqual("first note", db.WorkGetNote(item));
        db.WorkUpdateNote(item, "second note");
        Assert.AreEqual("second note", db.WorkGetNote(item));
        db.WorkDeleteNote(item);
        Assert.IsNull(db.WorkGetNote(item));
    }

    // ---------- WorkItem↔WorkTag 链接 ----------

    [TestMethod]
    public void WorkItemAddTag_GetItemTags()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        var tag = db.CreateWorkTag("p", true, 0);
        Assert.IsTrue(db.WorkItemAddTag(item, tag));
        var tags = db.GetWorkItemTags(item).ToList();
        CollectionAssert.AreEquivalent(new[] { "p" }, tags.Select(t => t.Name).ToList());
    }

    [TestMethod]
    public void WorkItemAddTag_Duplicate_ReturnsFalse()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        var tag = db.CreateWorkTag("p", true, 0);
        Assert.IsTrue(db.WorkItemAddTag(item, tag));
        Assert.IsFalse(db.WorkItemAddTag(item, tag));
    }

    [TestMethod]
    public void WorkItemRemoveTag_RemovesIt()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        var tag = db.CreateWorkTag("p", true, 0);
        db.WorkItemAddTag(item, tag);
        Assert.IsTrue(db.WorkItemRemoveTag(item, tag));
        Assert.IsFalse(db.GetWorkItemTags(item).Any());
    }

    [TestMethod]
    public void WorkItemCleanTags_RemovesAll()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        var p = db.CreateWorkTag("p", true, 0);
        var s = db.CreateWorkTag("s", false, 0);
        db.WorkItemAddTag(item, p);
        db.WorkItemAddTag(item, s);
        Assert.IsTrue(db.WorkItemCleanTags(item));
        Assert.IsFalse(db.GetWorkItemTags(item).Any());
    }

    /// <summary>档 2 薄委托回归：GetWorkTagsByDate 按 workId 分组、tag 正确。</summary>
    [TestMethod]
    public void GetWorkTagsByDate_GroupsByWorkId()
    {
        using var db = CreateDb();
        var item1 = db.CreateWorkItem("2026-08-01", "a");
        var item2 = db.CreateWorkItem("2026-08-01", "b");
        var p = db.CreateWorkTag("p", true, 0);
        var s = db.CreateWorkTag("s", false, 0);
        db.WorkItemAddTag(item1, p);
        db.WorkItemAddTag(item1, s);

        var dict = db.GetWorkTagsByDate("2026-08-01");
        Assert.AreEqual(1, dict.Count);
        CollectionAssert.AreEquivalent(new[] { "p", "s" }, dict[item1.Id].Select(t => t.Name).ToList());
        Assert.IsFalse(dict.ContainsKey(item2.Id));
    }

    [TestMethod]
    public void GetWorkTagsByWorkItemIds_GroupsRequestedItemsInOneContract()
    {
        using var db = CreateDb();
        var tagged = db.CreateWorkItem("2026-08-01", "tagged");
        var untagged = db.CreateWorkItem("2026-08-02", "untagged");
        var primary = db.CreateWorkTag("primary", true, 0);
        var secondary = db.CreateWorkTag("secondary", false, 0);
        db.WorkItemAddTag(tagged, primary);
        db.WorkItemAddTag(tagged, secondary);

        var result = db.GetWorkTagsByWorkItemIds(
            new[] { tagged.Id, untagged.Id, tagged.Id, int.MaxValue });

        Assert.AreEqual(1, result.Count);
        CollectionAssert.AreEqual(
            new[] { "primary", "secondary" },
            result[tagged.Id].Select(tag => tag.Name).ToArray());
        Assert.IsFalse(result.ContainsKey(untagged.Id));
    }

    [TestMethod]
    public void GetWorkTagsByWorkItemIds_EmptyInputReturnsEmptyResult()
    {
        using var db = CreateDb();

        var result = db.GetWorkTagsByWorkItemIds(Array.Empty<int>());

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void GetWorkTagsByWorkItemIds_LargeInputUsesSafeBatchesAndStableTagOrder()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "tagged");
        var secondaryFirst = db.CreateWorkTag("secondary-first", false, 0);
        var primary = db.CreateWorkTag("primary", true, 0);
        var secondarySecond = db.CreateWorkTag("secondary-second", false, 0);
        db.WorkItemAddTag(item, secondarySecond);
        db.WorkItemAddTag(item, primary);
        db.WorkItemAddTag(item, secondaryFirst);
        var ids = Enumerable.Range(100_000, 1_200).Append(item.Id).ToArray();

        var result = db.GetWorkTagsByWorkItemIds(ids);

        CollectionAssert.AreEqual(
            new[] { "primary", "secondary-first", "secondary-second" },
            result[item.Id].Select(tag => tag.Name).ToArray());
    }

    [TestMethod]
    public void QueryWorkItems_IgnoreTags_IncludesDateRangeEnds()
    {
        using var db = CreateDb();
        db.CreateWorkItem("2026-08-01", "begin");
        db.CreateWorkItem("2026-08-05", "end");
        db.CreateWorkItem("2026-08-06", "outside");

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            StartDate = "2026-08-01",
            EndDate = "2026-08-05",
        });

        CollectionAssert.AreEqual(new[] { "begin", "end" }, items.Select(item => item.Comment).ToArray());
    }

    [TestMethod]
    public void QueryWorkItems_InvalidTagFilterIsRejectedInsteadOfIgnored()
    {
        using var db = CreateDb();
        db.CreateWorkItem("2026-08-01", "must not leak");

        Assert.ThrowsExactly<ArgumentException>(() => db.QueryWorkItems(new WorkItemQuery
        {
            TagFilter = (WorkItemTagFilter)99,
        }));
    }

    [TestMethod]
    public void QueryWorkItems_AnyTag_ReturnsEachMatchingItemOnce()
    {
        using var db = CreateDb();
        var both = db.CreateWorkItem("2026-08-01", "both");
        var one = db.CreateWorkItem("2026-08-02", "one");
        db.CreateWorkItem("2026-08-03", "none");
        var first = db.CreateWorkTag("first", true, 0);
        var second = db.CreateWorkTag("second", false, 0);
        db.WorkItemAddTag(both, first);
        db.WorkItemAddTag(both, second);
        db.WorkItemAddTag(one, first);

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            TagIds = new[] { first.Id, second.Id, first.Id },
            TagFilter = WorkItemTagFilter.Any,
        });

        CollectionAssert.AreEqual(new[] { "both", "one" }, items.Select(item => item.Comment).ToArray());
    }

    [TestMethod]
    public void QueryWorkItems_AllTags_RequiresEverySelectedTag()
    {
        using var db = CreateDb();
        var both = db.CreateWorkItem("2026-08-01", "both");
        var one = db.CreateWorkItem("2026-08-02", "one");
        var first = db.CreateWorkTag("first", true, 0);
        var second = db.CreateWorkTag("second", false, 0);
        db.WorkItemAddTag(both, first);
        db.WorkItemAddTag(both, second);
        db.WorkItemAddTag(one, first);

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            TagIds = new[] { first.Id, second.Id },
            TagFilter = WorkItemTagFilter.All,
        });

        Assert.AreEqual("both", items.Single().Comment);
    }

    [TestMethod]
    public void QueryWorkItems_None_ReturnsOnlyUntaggedItems()
    {
        using var db = CreateDb();
        var tagged = db.CreateWorkItem("2026-08-01", "tagged");
        db.CreateWorkItem("2026-08-02", "untagged");
        var tag = db.CreateWorkTag("tag", true, 0);
        db.WorkItemAddTag(tagged, tag);

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            TagFilter = WorkItemTagFilter.None,
        });

        Assert.AreEqual("untagged", items.Single().Comment);
    }

    [TestMethod]
    public void QueryWorkItems_Exact_RequiresIdenticalTagSet()
    {
        using var db = CreateDb();
        var exact = db.CreateWorkItem("2026-08-01", "exact");
        var extra = db.CreateWorkItem("2026-08-02", "extra");
        var missing = db.CreateWorkItem("2026-08-03", "missing");
        var first = db.CreateWorkTag("first", true, 0);
        var second = db.CreateWorkTag("second", false, 0);
        var third = db.CreateWorkTag("third", false, 0);
        foreach (var item in new[] { exact, extra })
        {
            db.WorkItemAddTag(item, first);
            db.WorkItemAddTag(item, second);
        }
        db.WorkItemAddTag(extra, third);
        db.WorkItemAddTag(missing, first);

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            TagIds = new[] { first.Id, second.Id },
            TagFilter = WorkItemTagFilter.Exact,
        });

        Assert.AreEqual("exact", items.Single().Comment);
    }

    [TestMethod]
    public void QueryWorkItems_ExactWithNoTags_ReturnsUntaggedItems()
    {
        using var db = CreateDb();
        var tagged = db.CreateWorkItem("2026-08-01", "tagged");
        db.CreateWorkItem("2026-08-02", "untagged");
        var tag = db.CreateWorkTag("tag", true, 0);
        db.WorkItemAddTag(tagged, tag);

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            TagFilter = WorkItemTagFilter.Exact,
        });

        Assert.AreEqual("untagged", items.Single().Comment);
    }

    [TestMethod]
    public void QueryWorkItems_AnyOrAllWithNoTags_IsRejected()
    {
        using var db = CreateDb();
        db.CreateWorkItem("2026-08-01", "item");

        Assert.ThrowsExactly<ArgumentException>(() => db.QueryWorkItems(new WorkItemQuery
        {
            TagFilter = WorkItemTagFilter.Any,
        }));
        Assert.ThrowsExactly<ArgumentException>(() => db.QueryWorkItems(new WorkItemQuery
        {
            TagFilter = WorkItemTagFilter.All,
        }));
    }

    [TestMethod]
    public void QueryWorkItems_TextSearchesCommentAndNoteCaseInsensitively()
    {
        using var db = CreateDb();
        db.CreateWorkItem("2026-08-01", "Contains KEYWORD");
        var noteMatch = db.CreateWorkItem("2026-08-02", "other");
        db.WorkUpdateNote(noteMatch, "keyword in note");
        db.CreateWorkItem("2026-08-03", "unrelated");

        var items = db.QueryWorkItems(new WorkItemQuery { Text = "keyword" });

        CollectionAssert.AreEqual(
            new[] { "Contains KEYWORD", "other" },
            items.Select(item => item.Comment).ToArray());
    }

    [TestMethod]
    public void QueryWorkItems_PriorityCombinesWithTagFilter()
    {
        using var db = CreateDb();
        var match = db.CreateWorkItem("2026-08-01", "match");
        match.Priority = WorkPriorities.P2;
        db.UpdateWorkItem(match);
        var wrongPriority = db.CreateWorkItem("2026-08-02", "wrong priority");
        var tag = db.CreateWorkTag("tag", true, 0);
        db.WorkItemAddTag(match, tag);
        db.WorkItemAddTag(wrongPriority, tag);

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            TagIds = new[] { tag.Id },
            TagFilter = WorkItemTagFilter.Any,
            Priority = WorkPriorities.P2,
        });

        Assert.AreEqual("match", items.Single().Comment);
    }

    [TestMethod]
    public void QueryWorkItems_PaginationUsesStableDateAndIdOrder()
    {
        using var db = CreateDb();
        db.CreateWorkItem("2026-08-02", "third");
        db.CreateWorkItem("2026-08-01", "first");
        db.CreateWorkItem("2026-08-01", "second");

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            Limit = 1,
            Offset = 1,
        });

        Assert.AreEqual("second", items.Single().Comment);
    }

    /// <summary>档 2 薄委托回归：GetWorkNotesByDate 按 workId 分组。</summary>
    [TestMethod]
    public void GetWorkNotesByDate_GroupsByWorkId()
    {
        using var db = CreateDb();
        var item1 = db.CreateWorkItem("2026-08-01", "a");
        var item2 = db.CreateWorkItem("2026-08-01", "b");
        db.WorkUpdateNote(item1, "note1");

        var dict = db.GetWorkNotesByDate("2026-08-01");
        Assert.AreEqual(1, dict.Count);
        Assert.AreEqual("note1", dict[item1.Id]);
        Assert.IsFalse(dict.ContainsKey(item2.Id));
    }

    // ---------- RedMine ----------

    [TestMethod]
    public void AddRedMineActivity_GetActivities()
    {
        using var db = CreateDb();
        var act = GetRedMine(db).AddRedMineActivity(10, "开发");
        Assert.AreEqual(10, act.Id);
        Assert.AreEqual("开发", act.Title);
        var all = GetRedMine(db).GetRedMineActivities();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("开发", all.First().Title);
    }

    [TestMethod]
    public void AddRedMineProject_GetProjects()
    {
        using var db = CreateDb();
        var proj = GetRedMine(db).AddRedMineProject(20, "项目A", "描述");
        Assert.AreEqual(20, proj.Id);
        Assert.AreEqual("项目A", proj.Title);
        Assert.AreEqual("描述", proj.Description);
        Assert.IsFalse(proj.IsClosed);
        Assert.AreEqual(1, GetRedMine(db).GetRedMineProjects().Count);
    }

    [TestMethod]
    public void UpdateRedMineProjectStatus_TogglesClosed()
    {
        using var db = CreateDb();
        GetRedMine(db).AddRedMineProject(20, "P", "");
        GetRedMine(db).UpdateRedMineProjectStatus(20, true);
        Assert.IsTrue(GetRedMine(db).GetRedMineProjects().Single(p => p.Id == 20).IsClosed);
    }

    [TestMethod]
    public void AddRedMineIssue_GetIssuesByProject()
    {
        using var db = CreateDb();
        GetRedMine(db).AddRedMineProject(20, "项目A", "");
        GetRedMine(db).AddRedMineProject(21, "项目B", "");
        GetRedMine(db).AddRedMineIssue(100, "任务1", "张三", 20);
        GetRedMine(db).AddRedMineIssue(101, "任务2", "李四", 21);

        var all = GetRedMine(db).GetRedMineIssues(null).ToList();
        Assert.AreEqual(2, all.Count);

        var onlyA = GetRedMine(db).GetRedMineIssues(GetRedMine(db).GetRedMineProjects().Single(p => p.Id == 20)).ToList();
        Assert.AreEqual(1, onlyA.Count);
        Assert.AreEqual(100, onlyA[0].Id);
        Assert.AreEqual("项目A", onlyA[0].Project);
    }

    [TestMethod]
    public void UpdateRedMineIssueStatus_TogglesClosed()
    {
        using var db = CreateDb();
        GetRedMine(db).AddRedMineProject(20, "P", "");
        GetRedMine(db).AddRedMineIssue(100, "任务", "", 20);
        GetRedMine(db).UpdateRedMineIssueStatus(100, true);
        var issue = GetRedMine(db).GetRedMineIssues(null).Single(i => i.Id == 100);
        Assert.IsTrue(issue.Disabled);
    }

    [TestMethod]
    public void CreateWorkTimeEntry_WorkItemGetTimeEntry()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        GetRedMine(db).AddRedMineActivity(10, "开发");
        GetRedMine(db).AddRedMineProject(20, "P", "");
        GetRedMine(db).AddRedMineIssue(100, "任务", "", 20);

        var entry = GetRedMine(db).CreateWorkTimeEntry(item.Id, 10, 100);
        Assert.IsNotNull(entry);
        Assert.AreEqual(item.Id, entry!.WorkId);
        Assert.AreEqual(10, entry.ActivityId);
        Assert.AreEqual(100, entry.IssueId);

        var got = GetRedMine(db).WorkItemGetTimeEntry(item);
        Assert.IsNotNull(got);
        Assert.AreEqual(10, got!.ActivityId);
    }

    /// <summary>helper 边界：WorkItemWasUploaded 依赖 Exists；id=0 时未上传，id>0 时已上传。</summary>
    [TestMethod]
    public void WorkItemWasUploaded_ReflectsEntryId()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        GetRedMine(db).AddRedMineActivity(10, "开发");
        GetRedMine(db).AddRedMineProject(20, "P", "");
        GetRedMine(db).AddRedMineIssue(100, "任务", "", 20);

        GetRedMine(db).CreateWorkTimeEntry(item.Id, 10, 100);
        Assert.IsFalse(GetRedMine(db).WorkItemWasUploaded(item));

        var entry = GetRedMine(db).WorkItemGetTimeEntry(item)!;
        entry.EntryId = 42;
        Assert.IsTrue(GetRedMine(db).UpdateWorkTimeEntry(entry));
        Assert.IsTrue(GetRedMine(db).WorkItemWasUploaded(item));
    }

    /// <summary>RedMine 批量绑定回归：GetWorkTimeEntriesByDate 按 workId 分组。</summary>
    [TestMethod]
    public void GetWorkTimeEntriesByDate_GroupsByWorkId()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        GetRedMine(db).AddRedMineActivity(10, "开发");
        GetRedMine(db).AddRedMineProject(20, "P", "");
        GetRedMine(db).AddRedMineIssue(100, "任务", "", 20);
        GetRedMine(db).CreateWorkTimeEntry(item.Id, 10, 100);

        var dict = GetRedMine(db).GetWorkTimeEntriesByDate("2026-08-01");
        Assert.AreEqual(1, dict.Count);
        Assert.AreEqual(10, dict[item.Id].ActivityId);
    }

    [TestMethod]
    public void GetWorkTimeEntriesByWorkItemIds_ReturnsOnlyRequestedBindings()
    {
        using var db = CreateDb();
        var included = db.CreateWorkItem("2026-08-01", "included");
        var unbound = db.CreateWorkItem("2026-08-01", "unbound");
        var excluded = db.CreateWorkItem("2026-08-02", "excluded");
        var redmine = GetRedMine(db);
        redmine.AddRedMineActivity(10, "开发");
        redmine.AddRedMineProject(20, "P", "");
        redmine.AddRedMineIssue(100, "任务", "", 20);
        redmine.CreateWorkTimeEntry(included.Id, 10, 100);
        redmine.CreateWorkTimeEntry(excluded.Id, 10, 100);

        var entries = redmine.GetWorkTimeEntriesByWorkItemIds([included.Id, unbound.Id]);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(100, entries[included.Id].IssueId);
        Assert.IsFalse(entries.ContainsKey(excluded.Id));
    }

    [TestMethod]
    public void JiraGetWorkTimeEntriesByWorkItemIds_ReturnsOnlyRequestedBindings()
    {
        using var db = CreateDb();
        var included = db.CreateWorkItem("2026-08-01", "included");
        var unbound = db.CreateWorkItem("2026-08-01", "unbound");
        var excluded = db.CreateWorkItem("2026-08-02", "excluded");
        var jira = GetJira(db);
        jira.UpsertProject(new JiraProject("CDP", "CDP", "Performance", false));
        jira.UpsertIssue(new JiraIssue("CDP-1", "Included", "CDP", "CDP", "Doing", false));
        jira.UpsertIssue(new JiraIssue("CDP-2", "Excluded", "CDP", "CDP", "Doing", false));
        jira.CreateWorkTimeEntry(included.Id, "CDP-1");
        jira.CreateWorkTimeEntry(excluded.Id, "CDP-2");

        var entries = jira.GetWorkTimeEntriesByWorkItemIds([included.Id, unbound.Id]);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("CDP-1", entries[included.Id].IssueKey);
        Assert.IsFalse(entries.ContainsKey(excluded.Id));
    }

    // ---------- 统计 ----------

    /// <summary>档 2 上提回归 + helper 边界：空表 → 空 fallback（IsDBNull 守卫）。</summary>
    [TestMethod]
    public void GetStatistics_NoArg_EmptyTable_ReturnsFallback()
    {
        using var db = CreateDb();
        var stats = db.GetStatistics();
        Assert.AreEqual(0.0, stats.Total);
        Assert.AreEqual(0, stats.PrimaryTags.Count);
    }

    [TestMethod]
    public void GetStatistics_Range_SumsHours()
    {
        using var db = CreateDb();
        var a = db.CreateWorkItem("2026-08-01", "a"); a.Time = 2.0; db.UpdateWorkItem(a);
        var b = db.CreateWorkItem("2026-08-05", "b"); b.Time = 3.5; db.UpdateWorkItem(b);
        db.CreateWorkItem("2026-08-10", "c");

        var stats = db.GetStatistics("2026-08-01", "2026-08-05");
        Assert.AreEqual(5.5, stats.Total);
    }

    [TestMethod]
    public void GetStatistics_NoArg_AggregatesWholeRange()
    {
        using var db = CreateDb();
        var a = db.CreateWorkItem("2026-08-01", "a"); a.Time = 1.5; db.UpdateWorkItem(a);
        var stats = db.GetStatistics();
        Assert.AreEqual(1.5, stats.Total);
    }

    [TestMethod]
    public void GetStatistics_PrimaryTagsGrouped()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x"); item.Time = 4.0; db.UpdateWorkItem(item);
        var p = db.CreateWorkTag("主", true, 0);
        db.WorkItemAddTag(item, p);

        var stats = db.GetStatistics("2026-08-01", "2026-08-01");
        Assert.AreEqual(4.0, stats.Total);
        Assert.AreEqual(1, stats.PrimaryTags.Count);
        Assert.AreEqual(4.0, stats.PrimaryTags.First().Time);
    }

    // ---------- 版本 / 事务 / 工具 + helper 边界 ----------

    [TestMethod]
    public void Compatibility_InitializedDatabase_IsCompatibleAndPersistsFingerprint()
    {
        using var db = CreateDb();

        var report = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.AreEqual(DbCompatibilityState.Compatible, report.State, report.ToUserMessage());
        Assert.AreEqual(report.ExpectedSchema.Fingerprint, report.ActualSchema.Fingerprint);
        Assert.IsTrue(db.PersistCompatibilityMetadata(report));

        var rechecked = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.AreEqual(DbCompatibilityState.Compatible, rechecked.State, rechecked.ToUserMessage());
        Assert.IsNotNull(rechecked.Metadata);
        Assert.AreEqual(report.ActualSchema.Fingerprint, rechecked.Metadata!.SchemaFingerprint);
    }

    [TestMethod]
    public void Compatibility_DroppedRequiredIndex_IsReportedAsSchemaDrift()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.ExecRaw("DROP INDEX IF EXISTS idx_work_items_date;"));
        try
        {
            var report = db.CheckCompatibility(DataVersion.VersionCode);
            Assert.AreEqual(DbCompatibilityState.SchemaDrift, report.State);
            Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-SCHEMA-INDEX-MISSING"));
        }
        finally
        {
            Assert.IsTrue(db.ExecRaw("CREATE INDEX IF NOT EXISTS idx_work_items_date ON work_items(create_date);"));
        }
    }

    [TestMethod]
    public void Compatibility_NewerDataVersion_IsRejectedBeforeWrite()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.ExecRaw("DELETE FROM diary_schema_metadata;"));
        Assert.IsTrue(db.ExecRaw("DELETE FROM data_versions; INSERT INTO data_versions VALUES(999999);"));

        var report = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.AreEqual(DbCompatibilityState.NewerThanApplication, report.State);
        Assert.IsFalse(report.IsUsable);
    }

    [TestMethod]
    public void Compatibility_MigrationUnavailable_IsReported()
    {
        using var db = CreateDb();

        var report = db.CheckCompatibility(DataVersion.VersionCode + 1);

        Assert.AreEqual(DbCompatibilityState.MigrationUnavailable, report.State, report.ToUserMessage());
        Assert.IsFalse(report.IsUsable);
        Assert.IsFalse(report.CanMigrate);
    }

    [TestMethod]
    public void Compatibility_MetadataRunning_IsRejected()
    {
        using var db = CreateDb();
        var stable = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.IsTrue(db.PersistCompatibilityMetadata(stable));
        Assert.IsTrue(db.ExecRaw(
            "UPDATE diary_schema_metadata SET migration_state='Running', last_migration_id='test-running';"));

        var report = db.CheckCompatibility(DataVersion.VersionCode);

        Assert.AreEqual(DbCompatibilityState.MigrationIncomplete, report.State, report.ToUserMessage());
        Assert.AreEqual(DbMigrationState.Running, report.Metadata!.MigrationState);
        Assert.IsFalse(report.IsUsable);
    }

    [TestMethod]
    public void Compatibility_MetadataFailed_IsRejected()
    {
        using var db = CreateDb();
        var stable = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.IsTrue(db.PersistCompatibilityMetadata(stable));
        Assert.IsTrue(db.ExecRaw(
            "UPDATE diary_schema_metadata SET migration_state='Failed', last_migration_id='test-failed', last_error='boom';"));

        var report = db.CheckCompatibility(DataVersion.VersionCode);

        Assert.AreEqual(DbCompatibilityState.MigrationIncomplete, report.State, report.ToUserMessage());
        Assert.AreEqual(DbMigrationState.Failed, report.Metadata!.MigrationState);
        StringAssert.Contains(report.ToUserMessage(), "boom");
    }

    [TestMethod]
    public void Compatibility_MetadataProviderMismatch_IsRejected()
    {
        using var db = CreateDb();
        var stable = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.IsTrue(db.PersistCompatibilityMetadata(stable));
        Assert.IsTrue(db.ExecRaw("UPDATE diary_schema_metadata SET provider_id='OtherProvider';"));

        var report = db.CheckCompatibility(DataVersion.VersionCode);

        Assert.AreEqual(DbCompatibilityState.ProviderMismatch, report.State, report.ToUserMessage());
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-METADATA-PROVIDER-MISMATCH"));
    }

    [TestMethod]
    public void Compatibility_MetadataVersionMismatch_BlocksMigration()
    {
        using var db = CreateDb();
        var stable = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.IsTrue(db.PersistCompatibilityMetadata(stable));
        Assert.IsTrue(db.ExecRaw("DELETE FROM data_versions; INSERT INTO data_versions VALUES(65535);"));

        var report = db.CheckCompatibility(DataVersion.VersionCode);

        Assert.AreEqual(DbCompatibilityState.SchemaDrift, report.State, report.ToUserMessage());
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-METADATA-VERSION-MISMATCH"));
    }

    [TestMethod]
    public void Compatibility_ExtraIndex_DoesNotChangeCoreFingerprint()
    {
        using var db = CreateDb();
        var before = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.IsTrue(db.ExecRaw("CREATE INDEX compatibility_extra_index ON work_items(comment);"));

        try
        {
            var report = db.CheckCompatibility(DataVersion.VersionCode);
            Assert.AreEqual(DbCompatibilityState.Compatible, report.State, report.ToUserMessage());
            Assert.AreEqual(before.ActualSchema.Fingerprint, report.ActualSchema.Fingerprint);
        }
        finally
        {
            Assert.IsTrue(db.ExecRaw("DROP INDEX IF EXISTS compatibility_extra_index;"));
        }
    }

    [TestMethod]
    public void Compatibility_MissingRequiredTable_IsSchemaDrift()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.ExecRaw("DROP TABLE work_notes;"));

        var report = db.CheckCompatibility(DataVersion.VersionCode);

        Assert.AreEqual(DbCompatibilityState.SchemaDrift, report.State, report.ToUserMessage());
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-SCHEMA-TABLE-MISSING"));
    }

    [TestMethod]
    public void Compatibility_MissingRequiredColumn_IsSchemaDrift()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.ExecRaw("ALTER TABLE work_items DROP COLUMN is_read_only;"));

        try
        {
            var report = db.CheckCompatibility(DataVersion.VersionCode);
            Assert.AreEqual(DbCompatibilityState.SchemaDrift, report.State, report.ToUserMessage());
            Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-SCHEMA-COLUMN-MISSING"));
        }
        finally
        {
            var columnDefinition = db.GetProviderInfo().ProviderId == "PostgreSQL"
                ? "BOOLEAN NOT NULL DEFAULT FALSE"
                : "INTEGER NOT NULL DEFAULT 0";
            Assert.IsTrue(db.ExecRaw(
                $"ALTER TABLE work_items ADD COLUMN is_read_only {columnDefinition};"));
        }
    }

    [TestMethod]
    public void Compatibility_WrongIndexDefinition_IsSchemaDrift()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.ExecRaw(
            "DROP INDEX idx_work_items_date; " +
            "CREATE INDEX idx_work_items_date ON work_items(comment);"));

        try
        {
            var report = db.CheckCompatibility(DataVersion.VersionCode);
            Assert.AreEqual(DbCompatibilityState.SchemaDrift, report.State, report.ToUserMessage());
            Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-SCHEMA-INDEX-MISSING"));
        }
        finally
        {
            Assert.IsTrue(db.ExecRaw(
                "DROP INDEX IF EXISTS idx_work_items_date; " +
                "CREATE INDEX idx_work_items_date ON work_items(create_date);"));
        }
    }

    [TestMethod]
    public void Compatibility_ClosedDatabase_IsUnavailable()
    {
        using var db = CreateDb();
        db.Close();

        var report = db.CheckCompatibility(DataVersion.VersionCode);

        Assert.AreEqual(DbCompatibilityState.Unavailable, report.State);
        Assert.IsFalse(report.IsUsable);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-COMPATIBILITY-CHECK-FAILED"));
    }

    [TestMethod]
    public void Compatibility_RegisteredFingerprintDrift_BlocksMigration()
    {
        using var db = CreateDb(version => version switch
        {
            0x0FFFF => new TestMigration(0x0FFFF, 0x10000, MigrationResult.Success),
            _ => GetProductionMigration(version),
        });
        var migration = db.MigrateTo(DataVersion.VersionCode, new DbMigrationOptions(CreateBackup: false));
        Assert.IsTrue(migration.Success, migration.Error);

        var stable = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.AreEqual(DbCompatibilityState.Compatible, stable.State, stable.ToUserMessage());
        Assert.IsTrue(db.PersistCompatibilityMetadata(stable));
        Assert.IsTrue(db.ExecRaw("DROP INDEX IF EXISTS idx_work_items_date;"));
        Assert.IsTrue(db.ExecRaw("DELETE FROM data_versions; INSERT INTO data_versions VALUES(65535);"));

        var report = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.AreEqual(DbCompatibilityState.SchemaDrift, report.State, report.ToUserMessage());
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-SCHEMA-FINGERPRINT-MISMATCH"));
    }

    [TestMethod]
    public void GetDataVersion_DefaultDatabaseIsMigratedToCurrentVersion()
    {
        using var db = CreateDb();
        Assert.AreEqual(DataVersion.VersionCode, db.GetDataVersion());
    }

    [TestMethod]
    public void UpdateTables_CurrentVersionMatch_ReturnsTrueNoOp()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.UpdateTables(DataVersion.VersionCode));
    }

    [TestMethod]
    public void UpdateTables_NoMigrationForTarget_ReturnsFalse()
    {
        using var db = CreateDb();
        Assert.IsFalse(db.UpdateTables(0x99999));
    }

    [TestMethod]
    public void UpdateTables_ContinuousMigrations_CommitsEveryStep()
    {
        var migrations = new Dictionary<uint, Migration>
        {
            [0x10000] = new TestMigration(0x10000, 0x10001, MigrationResult.Success),
            [0x10001] = new TestMigration(0x10001, 0x10002, MigrationResult.Success),
        };
        using var db = CreateDb(version => migrations.GetValueOrDefault(version));

        Assert.IsTrue(db.UpdateTables(0x10002));
        Assert.AreEqual(0x10002u, db.GetDataVersion());
        var history = ReadMigrationHistory(db);
        Assert.AreEqual(2, history.Count);
        Assert.IsTrue(history.All(entry => entry.Success));
        CollectionAssert.AreEqual(
            new[] { migrations[0x10000].Id, migrations[0x10001].Id },
            history.Select(entry => entry.MigrationId).ToArray());
    }

    [TestMethod]
    public void MigrateTo_Success_WritesStableMetadataAndHistory()
    {
        var migration = new TestMigration(0x10000, 0x10001, MigrationResult.Success);
        using var db = CreateDb(_ => migration);

        var result = db.MigrateTo(0x10001);
        var report = db.CheckCompatibility(0x10001);
        var history = ReadMigrationHistory(db);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(DbCompatibilityState.Compatible, report.State, report.ToUserMessage());
        Assert.AreEqual(DbMigrationState.Stable, report.Metadata!.MigrationState);
        Assert.AreEqual(0x10001u, report.Metadata.SchemaVersion);
        Assert.IsNull(report.Metadata.LastMigrationId);
        Assert.IsNull(report.Metadata.LastError);
        Assert.AreEqual(1, history.Count);
        Assert.AreEqual(migration.Id, history[0].MigrationId);
        Assert.AreEqual(0x10000u, history[0].VersionFrom);
        Assert.AreEqual(0x10001u, history[0].VersionTo);
        Assert.AreEqual(migration.Checksum, history[0].Checksum);
        Assert.IsTrue(history[0].Success);
        Assert.IsNull(history[0].Error);
    }

    [TestMethod]
    public void MigrateTo_Failure_WritesFailedMetadataAndHistory()
    {
        var migration = new TestMigration(0x10000, 0x10001, MigrationResult.ThrowAfterWrite);
        using var db = CreateDb(_ => migration);

        var result = db.MigrateTo(0x10001);
        var report = db.CheckCompatibility(0x10001);
        var history = ReadMigrationHistory(db);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(DbCompatibilityState.MigrationIncomplete, report.State, report.ToUserMessage());
        Assert.AreEqual(DbMigrationState.Failed, report.Metadata!.MigrationState);
        Assert.AreEqual(0x10000u, report.Metadata.SchemaVersion);
        Assert.AreEqual(migration.Id, report.Metadata.LastMigrationId);
        StringAssert.Contains(report.Metadata.LastError!, "migration failed");
        Assert.AreEqual(1, history.Count);
        Assert.IsFalse(history[0].Success);
        StringAssert.Contains(history[0].Error!, "migration failed");
    }

    [TestMethod]
    public void MigrateTo_SecondStepFailure_PreservesFirstCommit()
    {
        var first = new TestMigration(0x10000, 0x10001, MigrationResult.Success);
        var second = new TestMigration(0x10001, 0x10002, MigrationResult.ThrowAfterWrite);
        var migrations = new Dictionary<uint, Migration>
        {
            [first.VersionFrom] = first,
            [second.VersionFrom] = second,
        };
        using var db = CreateDb(version => migrations.GetValueOrDefault(version));

        var result = db.MigrateTo(0x10002);
        var report = db.CheckCompatibility(0x10002);
        var history = ReadMigrationHistory(db);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0x10001u, db.GetDataVersion());
        Assert.AreEqual(DbCompatibilityState.MigrationIncomplete, report.State, report.ToUserMessage());
        Assert.AreEqual(0x10001u, report.Metadata!.SchemaVersion);
        Assert.AreEqual(DbMigrationState.Failed, report.Metadata.MigrationState);
        Assert.AreEqual(2, history.Count);
        Assert.IsTrue(history[0].Success);
        Assert.AreEqual(first.Id, history[0].MigrationId);
        Assert.IsFalse(history[1].Success);
        Assert.AreEqual(second.Id, history[1].MigrationId);
    }

    [TestMethod]
    public void UpdateTables_Success_PreservesExistingBusinessData()
    {
        using var db = CreateDb(_ =>
            new TestMigration(0x10000, 0x10001, MigrationResult.Success));
        var tag = db.CreateWorkTag("migration-tag", true, 0x123456);
        var item = db.CreateWorkItem("2026-08-15", "migration-item");
        item.Time = 2.5;
        db.UpdateWorkItem(item);
        db.WorkItemAddTag(item, tag);
        db.WorkUpdateNote(item, "migration-note");

        Assert.IsTrue(db.UpdateTables(0x10001));

        var restored = db.GetWorkItemByDate("2026-08-15").Single(x => x.Id == item.Id);
        Assert.AreEqual("migration-item", restored.Comment);
        Assert.AreEqual(2.5, restored.Time);
        Assert.AreEqual("migration-note", db.WorkGetNote(restored));
        Assert.IsTrue(db.GetWorkItemTags(restored).Any(x => x.Id == tag.Id));
    }

    [TestMethod]
    public void UpdateTables_UpReturnsFalse_RollsBackVersionWrite()
    {
        using var db = CreateDb(_ =>
            new TestMigration(0x10000, 0x10001, MigrationResult.FalseAfterWrite));
        var item = db.CreateWorkItem("2026-08-15", "preserved-after-failure");

        Assert.IsFalse(db.UpdateTables(0x10001));
        Assert.AreEqual(0x10000u, db.GetDataVersion());
        Assert.IsTrue(db.GetWorkItemByDate("2026-08-15").Any(x => x.Id == item.Id));
    }

    [TestMethod]
    public void UpdateTables_UpThrows_RollsBackVersionWrite()
    {
        using var db = CreateDb(_ =>
            new TestMigration(0x10000, 0x10001, MigrationResult.ThrowAfterWrite));

        Assert.IsFalse(db.UpdateTables(0x10001));
        Assert.AreEqual(0x10000u, db.GetDataVersion());
    }

    [TestMethod]
    public void UpdateTables_UpDoesNotAdvanceVersion_RollsBackAndStops()
    {
        var calls = 0;
        using var db = CreateDb(_ =>
            new TestMigration(0x10000, 0x10001, MigrationResult.NoVersionWrite, () => calls++));

        Assert.IsFalse(db.UpdateTables(0x10001));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0x10000u, db.GetDataVersion());
    }

    [TestMethod]
    public void UpdateTables_MigrationFromDoesNotMatchCurrent_RejectsBrokenChain()
    {
        var called = false;
        using var db = CreateDb(_ =>
            new TestMigration(0x10001, 0x10002, MigrationResult.Success, () => called = true));

        Assert.IsFalse(db.UpdateTables(0x10002));
        Assert.IsFalse(called);
        Assert.AreEqual(0x10000u, db.GetDataVersion());
    }

    [TestMethod]
    public void UpdateTables_DowngradeTarget_IsRejected()
    {
        using var db = CreateDb();

        Assert.IsFalse(db.UpdateTables(0x0FFFF));
        Assert.AreEqual(DataVersion.VersionCode, db.GetDataVersion());
    }

    [TestMethod]
    public void UpdateTables_MigrationOvershootsTarget_IsRejected()
    {
        var called = false;
        using var db = CreateDb(_ =>
            new TestMigration(0x10000, 0x10002, MigrationResult.Success, () => called = true));

        Assert.IsFalse(db.UpdateTables(0x10001));
        Assert.IsFalse(called);
        Assert.AreEqual(0x10000u, db.GetDataVersion());
    }

    [TestMethod]
    public void ExecRaw_RunsAndIsObservable()
    {
        using var db = CreateDb();
        // 用十进制字面量（Pg 不接受 0x 十六进制整型字面量）；0x20000 == 131072
        Assert.IsTrue(db.ExecRaw("INSERT INTO data_versions VALUES(131072);"));
        Assert.AreEqual(0x20000u, db.GetDataVersion());
    }

    [TestMethod]
    public void Transactions_LifecycleCommitAndRollback()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.BeginTransaction());
        Assert.IsTrue(db.CommitTransaction());
        Assert.IsTrue(db.BeginTransaction());
        Assert.IsTrue(db.RollbackTransaction());
    }

    protected sealed record MigrationHistorySnapshot(
        string MigrationId,
        uint VersionFrom,
        uint VersionTo,
        string Checksum,
        bool Success,
        string? Error);

    protected static IReadOnlyList<MigrationHistorySnapshot> ReadMigrationHistory(DbInterfaceBase db)
        => ((IDbExtensionHost)db).Query(
            "SELECT migration_id, version_from, version_to, checksum, success, error " +
            "FROM diary_schema_migrations ORDER BY applied_at, migration_id;",
            reader => new MigrationHistorySnapshot(
                reader.GetString(0),
                Convert.ToUInt32(reader.GetValue(1)),
                Convert.ToUInt32(reader.GetValue(2)),
                reader.GetString(3),
                Convert.ToBoolean(reader.GetValue(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5)));

    protected enum MigrationResult
    {
        Success,
        FalseAfterWrite,
        ThrowAfterWrite,
        NoVersionWrite,
    }

    protected sealed class TestMigration(
        uint from,
        uint to,
        MigrationResult result,
        Action? onUp = null) : Migration(from, to)
    {
        public override bool Up(DbInterfaceBase db)
        {
            onUp?.Invoke();
            if (result == MigrationResult.NoVersionWrite)
                return true;

            if (VersionFrom == 0x10000 && VersionTo == 0x10001)
            {
                db.ExecRaw(
                    "ALTER TABLE tag_extra_field_definitions " +
                    "ADD COLUMN default_value TEXT NOT NULL DEFAULT '';");
            }

            db.ExecRaw($"INSERT INTO data_versions VALUES({VersionTo});");
            return result switch
            {
                MigrationResult.Success => true,
                MigrationResult.FalseAfterWrite => false,
                MigrationResult.ThrowAfterWrite => throw new InvalidOperationException("migration failed"),
                _ => throw new InvalidOperationException("unexpected migration result"),
            };
        }
    }

    [TestMethod]
    public void DropData_ClearsAllTables()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("p", true, 0);
        var definition = new TagExtraFieldDefinition
        {
            FieldKey = "drop-data.value",
            TagId = tag.Id,
            Label = "清理值",
            Type = TagExtraFieldType.Text,
        };
        Assert.IsTrue(db.CreateTagExtraFieldDefinition(definition));
        var item = db.CreateWorkItem("2026-08-01", "x");
        Assert.IsTrue(db.WorkItemAddTag(item, tag));
        Assert.IsTrue(db.SaveWorkItemExtraFieldValues(item.Id,
        [
            new WorkItemExtraFieldValue
            {
                WorkItemId = item.Id,
                FieldId = definition.FieldId,
                Value = "待清理",
            },
        ]));

        Assert.IsTrue(db.DropData());
        Assert.AreEqual(0, db.AllWorkTags().Count);
        Assert.AreEqual(0, db.GetWorkItemByDate("2026-08-01").Count);
        Assert.AreEqual(0, db.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true).Count);
        Assert.AreEqual(0, db.GetWorkItemExtraFields(item).Count);
    }

    /// <summary>helper 边界：Query 在空表上返回空列表。</summary>
    [TestMethod]
    public void GetRedMineActivities_EmptyTable_ReturnsEmptyList()
    {
        using var db = CreateDb();
        Assert.AreEqual(0, GetRedMine(db).GetRedMineActivities().Count);
    }

    [TestMethod]
    public void RedMineSchemaVersion_IsRecorded()
    {
        using var db = CreateDb();
        var redmine = GetRedMine(db);
        Assert.AreEqual(redmine.SchemaVersion, redmine.GetSchemaVersion());
    }

    /// <summary>helper 边界：无条目时 WorkItemGetTimeEntry 返回 null。</summary>
    [TestMethod]
    public void WorkItemGetTimeEntry_NoEntry_ReturnsNull()
    {
        using var db = CreateDb();
        var item = db.CreateWorkItem("2026-08-01", "x");
        Assert.IsNull(GetRedMine(db).WorkItemGetTimeEntry(item));
    }

    // ---------- 补全：GetWorkItemsByTagAndDate / KeepAlive / Close / 边界 ----------

    /// <summary>GetWorkItemsByTagAndDate 单标签分支（l2==0）。</summary>
    [TestMethod]
    public void GetWorkItemsByTagAndDate_SingleTag_ReturnsMatchingItems()
    {
        using var db = CreateDb();
        var p = db.CreateWorkTag("p", true, 0);
        var item1 = db.CreateWorkItem("2026-08-01", "with-tag");
        db.WorkItemAddTag(item1, p);
        var item2 = db.CreateWorkItem("2026-08-01", "no-tag");

        var result = db.GetWorkItemsByTagAndDate("2026-08-01", "2026-08-01", p.Id).ToList();
        CollectionAssert.AreEquivalent(new[] { item1.Id }, result.Select(i => i.Id).ToList());
        Assert.IsFalse(result.Any(i => i.Id == item2.Id));
    }

    /// <summary>GetWorkItemsByTagAndDate 双标签分支（l2!=0）：只返回同时含两个标签的项。</summary>
    [TestMethod]
    public void GetWorkItemsByTagAndDate_TwoTags_ReturnsItemsWithBoth()
    {
        using var db = CreateDb();
        var p = db.CreateWorkTag("p", true, 0);
        var s = db.CreateWorkTag("s", false, 0);
        var itemBoth = db.CreateWorkItem("2026-08-01", "both");
        db.WorkItemAddTag(itemBoth, p);
        db.WorkItemAddTag(itemBoth, s);
        var itemOnlyP = db.CreateWorkItem("2026-08-01", "only-p");
        db.WorkItemAddTag(itemOnlyP, p);

        var result = db.GetWorkItemsByTagAndDate("2026-08-01", "2026-08-01", p.Id, s.Id).ToList();
        CollectionAssert.AreEquivalent(new[] { itemBoth.Id }, result.Select(i => i.Id).ToList());
        Assert.IsFalse(result.Any(i => i.Id == itemOnlyP.Id));
    }

    [TestMethod]
    public void KeepAlive_ReturnsTrue()
    {
        using var db = CreateDb();
        // SQLite 空操作；Pg 空闲不足 30s 直接返回 true。两库均应返回 true。
        Assert.IsTrue(db.KeepAlive());
    }

    [TestMethod]
    public void Close_CanBeCalledWithoutThrowing()
    {
        using var db = CreateDb();
        db.Close();
        // using/Dispose 在已 Close 的库上应安全（_connection/_dataSource 已置 null）
    }

    [TestMethod]
    public void AddRedMineIssue_ClosedTrue_ReflectsInQuery()
    {
        using var db = CreateDb();
        GetRedMine(db).AddRedMineProject(20, "P", "");
        GetRedMine(db).AddRedMineIssue(100, "任务", "张三", 20, closed: true);
        var issue = GetRedMine(db).GetRedMineIssues(null).Single(i => i.Id == 100);
        Assert.IsTrue(issue.Disabled);
    }

    /// <summary>事务回滚真的回退数据（不只测生命周期返回值）。</summary>
    [TestMethod]
    public void Transaction_Rollback_RevertsData()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.BeginTransaction());
        db.CreateWorkTag("rollback-me", true, 0);
        Assert.IsTrue(db.RollbackTransaction());
        Assert.IsFalse(db.AllWorkTags().Any(t => t.Name == "rollback-me"));
    }
}
