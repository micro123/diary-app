using System.Data.Common;
using Diary.Core.Data.App;
using Diary.Core.Data.Base;
using Diary.Core.Data.Statistics;
using Diary.Database;
using Diary.ScriptHost;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptLogItemApiTests
{
    [TestMethod]
    public async Task Preview_DoesNotAccessDatabaseOrIdempotencyStore()
    {
        var api = new LogItemScriptApi(
            () => throw new AssertFailedException("Preview 不应访问数据库。"),
            new ThrowingIdempotencyStore());

        var result = await api.CreateAsync(new ScriptLogItemRequest(
            "2026-08-16",
            2.5,
            "预览记录",
            "预览备注",
            "preview-key",
            Preview: true));

        Assert.IsTrue(result.Succeeded, result.Error?.Message);
        Assert.IsNotNull(result.Item);
        Assert.AreEqual(0, result.Item.Id);
        Assert.AreEqual("预览记录", result.Item.Comment);
        Assert.IsTrue(result.Effects?.Preview);
        Assert.AreEqual(0, result.Effects?.AppendedCount);
    }

    [TestMethod]
    public async Task TemplatePreview_DoesNotAccessDatabaseOrIdempotencyStore()
    {
        var templateId = Guid.NewGuid().ToString("D");
        var api = new TemplateLogItemScriptApi(
            () => throw new AssertFailedException("模板 Preview 不应访问数据库。"),
            () =>
            [
                new Template
                {
                    Id = templateId,
                    Name = "模板",
                    DefaultTitle = "模板默认标题",
                    DefaultTime = 1,
                },
            ],
            new ThrowingIdempotencyStore());

        var result = await api.CreateAsync(new ScriptTemplateLogItemRequest(
            "2026-08-16",
            templateId,
            1.5,
            IdempotencyKey: "template-preview-key",
            Preview: true));

        Assert.IsTrue(result.Succeeded, result.Error?.Message);
        Assert.AreEqual("模板默认标题", result.Item?.Comment);
        Assert.IsTrue(result.Effects?.Preview);
        Assert.AreEqual(0, result.Effects?.AppendedCount);
    }

    [TestMethod]
    public async Task Create_CommitsWorkItemAndNote()
    {
        using var database = TestDatabase.Create();
        var api = new LogItemScriptApi(() => database);

        var result = await api.CreateAsync(new ScriptLogItemRequest(
            "2026-08-16",
            2.5,
            "事务记录",
            "事务备注"));

        Assert.IsTrue(result.Succeeded, result.Error?.Message);
        var item = database.GetWorkItemByDate("2026-08-16").Single(item => item.Id == result.Item!.Id);
        Assert.AreEqual(2.5, item.Time);
        Assert.AreEqual("事务备注", database.WorkGetNote(item));
    }

    [TestMethod]
    public async Task DuplicateRequest_DoesNotCreateAnotherWorkItem()
    {
        using var database = TestDatabase.Create();
        var store = new ScriptIdempotencyStore();
        var api = new LogItemScriptApi(() => database, store);
        var request = new ScriptLogItemRequest(
            "2026-08-16",
            1,
            "幂等记录",
            IdempotencyKey: "same-key");

        var first = await api.CreateAsync(request);
        var second = await api.CreateAsync(request);

        Assert.IsTrue(first.Succeeded, first.Error?.Message);
        Assert.IsTrue(second.Succeeded, second.Error?.Message);
        Assert.IsTrue(second.Duplicate);
        Assert.AreEqual(0, second.Effects?.AppendedCount);
        Assert.AreEqual(1, database.GetWorkItemByDate("2026-08-16").Count);
    }

    [TestMethod]
    public async Task Create_RollsBackWhenNoteWriteFails()
    {
        var database = new FailingNoteDatabase();
        var api = new LogItemScriptApi(() => database);

        var result = await api.CreateAsync(new ScriptLogItemRequest(
            "2026-08-16",
            1,
            "失败记录",
            "触发回滚"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ScriptLogItemErrorCode.ProviderFailure, result.Error?.Code);
        Assert.IsTrue(database.TransactionStarted);
        Assert.IsTrue(database.RollbackCalled);
        Assert.IsFalse(database.CommitCalled);
    }

    private sealed class ThrowingIdempotencyStore : IScriptIdempotencyStore
    {
        public IDisposable Acquire(string scope, string key) => throw new AssertFailedException("Preview 不应获取幂等锁。");

        public bool TryGet(string scope, string key, out ScriptLogItemResult result) =>
            throw new AssertFailedException("Preview 不应读取幂等结果。");

        public void Save(string scope, string key, ScriptLogItemResult result) =>
            throw new AssertFailedException("Preview 不应保存幂等结果。");
    }

    private sealed class FailingNoteDatabase : DbInterfaceBase
    {
        public FailingNoteDatabase() : base(new TestDatabaseFactory())
        {
        }

        public bool TransactionStarted { get; private set; }
        public bool CommitCalled { get; private set; }
        public bool RollbackCalled { get; private set; }

        public override bool Connect() => true;
        public override bool Initialized() => true;
        public override bool KeepAlive() => true;
        public override void Close() { }
        public override void Dispose() { }
        public override uint GetDataVersion() => 0;
        public override WorkTag CreateWorkTag(string name, bool primary, int color, IReadOnlyDictionary<string, string>? metadata = null) => throw new NotSupportedException();
        public override bool UpdateWorkTag(WorkTag tag) => throw new NotSupportedException();
        public override bool DeleteWorkTag(WorkTag tag) => throw new NotSupportedException();
        public override ICollection<WorkTag> AllWorkTags() => throw new NotSupportedException();
        public override bool UpdateWorkTagId(int oldId, int newId) => throw new NotSupportedException();
        public override WorkItem CreateWorkItem(string date, string comment) => new() { Id = 1, CreateDate = date, Comment = comment };
        public override bool UpdateWorkItem(WorkItem item) => true;
        public override bool DeleteWorkItem(WorkItem item) => throw new NotSupportedException();
        public override ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData) => throw new NotSupportedException();
        public override ICollection<WorkItem> GetWorkItemByDate(string data) => throw new NotSupportedException();
        public override ICollection<WorkItem> QueryWorkItems(WorkItemQuery query) => throw new NotSupportedException();
        public override bool UpdateWorkItemId(int oldId, int newId) => throw new NotSupportedException();
        public override bool MarkWorkItemReadOnly(WorkItem item) => throw new NotSupportedException();
        public override void WorkUpdateNote(WorkItem work, string content) => throw new InvalidOperationException("模拟备注写入失败。");
        public override void WorkDeleteNote(WorkItem work) => throw new NotSupportedException();
        public override string? WorkGetNote(WorkItem work) => null;
        public override bool WorkItemAddTag(WorkItem item, WorkTag tag) => throw new NotSupportedException();
        public override bool WorkItemRemoveTag(WorkItem item, WorkTag tag) => throw new NotSupportedException();
        public override bool WorkItemCleanTags(WorkItem item) => throw new NotSupportedException();
        public override ICollection<WorkTag> GetWorkItemTags(WorkItem item) => throw new NotSupportedException();
        public override StatisticsResult GetStatistics(string beginDate, string endDate) => throw new NotSupportedException();
        public override ICollection<WorkItem> GetWorkItemsByTagAndDate(string dateBegin, string dateEnd, int l1, int l2 = 0) => throw new NotSupportedException();
        public override bool DropData() => throw new NotSupportedException();

        public override bool BeginTransaction()
        {
            TransactionStarted = true;
            return true;
        }

        public override bool CommitTransaction()
        {
            CommitCalled = true;
            return true;
        }

        public override bool RollbackTransaction()
        {
            RollbackCalled = true;
            return true;
        }

        protected override DbCommand CreateCommand(string sql) => throw new NotSupportedException();
        protected override string ReadString(DbDataReader reader, int ordinal) => throw new NotSupportedException();
        protected override void BindParameter(DbCommand command, string name, object? value) => throw new NotSupportedException();
    }
}
