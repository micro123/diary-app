using Diary.Core;
using Diary.Core.Data.Base;
using Diary.Database;
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

    protected static IRedMineDb GetRedMine(DbInterfaceBase db, string instanceId = "redmine.default")
        => db.GetExtension<IRedMineDb>(instanceId, new RedMinePlugin().GetMigrations())!;

    // ---------- WorkTag ----------

    [TestMethod]
    public void CreateWorkTag_Primary_MapsFields()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("工作", true, 0x112233);
        Assert.IsTrue(tag.Id > 0);
        Assert.AreEqual("工作", tag.Name);
        Assert.AreEqual(0x112233, tag.Color);
        Assert.AreEqual(TagLevels.Primary, tag.Level);
        Assert.IsFalse(tag.Disabled);
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
        tag.Color = 99;
        tag.Level = TagLevels.Secondary;
        tag.Disabled = true;
        Assert.IsTrue(db.UpdateWorkTag(tag));
        var all = db.AllWorkTags();
        var got = all.Single(x => x.Id == tag.Id);
        Assert.AreEqual(99, got.Color);
        Assert.AreEqual(TagLevels.Secondary, got.Level);
        Assert.IsTrue(got.Disabled);
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
    public void GetDataVersion_DefaultIsInitialCode()
    {
        using var db = CreateDb();
        Assert.AreEqual(0x10000u, db.GetDataVersion());
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
    }

    [TestMethod]
    public void UpdateTables_UpReturnsFalse_RollsBackVersionWrite()
    {
        using var db = CreateDb(_ =>
            new TestMigration(0x10000, 0x10001, MigrationResult.FalseAfterWrite));

        Assert.IsFalse(db.UpdateTables(0x10001));
        Assert.AreEqual(0x10000u, db.GetDataVersion());
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
        Assert.AreEqual(0x10000u, db.GetDataVersion());
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

    private enum MigrationResult
    {
        Success,
        FalseAfterWrite,
        ThrowAfterWrite,
        NoVersionWrite,
    }

    private sealed class TestMigration(
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
        db.CreateWorkTag("p", true, 0);
        db.CreateWorkItem("2026-08-01", "x");
        Assert.IsTrue(db.DropData());
        Assert.AreEqual(0, db.AllWorkTags().Count);
        Assert.AreEqual(0, db.GetWorkItemByDate("2026-08-01").Count);
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
