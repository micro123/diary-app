using System.Reflection;
using Avalonia.Headless;
using Diary.App;
using Diary.App.Models;
using Diary.App.ViewModels;
using Diary.App.ViewModels.Dialogs;
using Diary.Core;
using Diary.Core.Data.App;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Db.SQLite;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
[DoNotParallelize]
public sealed class WorkEditorViewModelTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
    }

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public async Task UploadFromBackgroundThreadUpdatesResultsOnUiThread()
    {
        var coordinator = new RecordingUploadCoordinator();
        var viewModel = new WorkEditorViewModel(
            new DbShareData(NullLogger<DbShareData>.Instance),
            new NoopPersistenceCoordinator(),
            coordinator,
            new TrackerUiContributionRegistry(),
            "测试工作项",
            new NoopTagAutomationCoordinator());
        SetWorkItem(viewModel, new WorkItem
        {
            Id = 42,
            CreateDate = "2026-08-11",
            Comment = "测试工作项",
            Time = 1.5,
        });

        await _session.Dispatch(async () =>
        {
            var result = await Task.Run(viewModel.Upload);

            Assert.IsTrue(result.Item1);
            Assert.AreEqual(1, viewModel.UploadResults.Count);
            Assert.AreEqual(TrackerUploadState.Succeeded, viewModel.UploadResults[0].State);
            Assert.AreEqual(42, coordinator.LastItem?.Id);
        }, CancellationToken.None);
    }

    [TestMethod]
    public void TrackerEditorVisibilityMatchesAvailableExtensions()
    {
        Assert.IsFalse(CreateViewModel().HasTrackerEditors);
        Assert.IsTrue(CreateViewModel(CreateCloneTrackerRegistry()).HasTrackerEditors);
    }

    [TestMethod]
    public void RefreshTrackerEditorsUpdatesVisibilityAfterRegistryChanges()
    {
        var registry = new TrackerUiContributionRegistry();
        var viewModel = CreateViewModel(registry);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        registry.Register([new CloneTrackerContributionFactory()], [new CloneTrackerInstance()]);
        viewModel.RefreshTrackerEditors();

        Assert.IsTrue(viewModel.HasTrackerEditors);
        Assert.HasCount(1, viewModel.Extensions);
        Assert.HasCount(1, viewModel.TrackerTabs);
        CollectionAssert.Contains(changedProperties, nameof(viewModel.HasTrackerEditors));

        registry.Register([], []);
        viewModel.RefreshTrackerEditors();

        Assert.IsFalse(viewModel.HasTrackerEditors);
        Assert.IsEmpty(viewModel.Extensions);
        Assert.IsEmpty(viewModel.TrackerTabs);
    }

    [TestMethod]
    public void RefreshTrackerEditorsUsesBatchBindingAndPreservesUnsavedSelection()
    {
        var registry = CreateCloneTrackerRegistry();
        var viewModel = CreateViewModel(registry);
        LoadExistingItem(viewModel, new WorkItem
        {
            Id = 42,
            CreateDate = "2026-08-25",
            Comment = "刷新 Tracker",
        });
        var previous = (CloneTrackerExtension)viewModel.Extensions.Single();
        previous.Selection = "ISSUE-42";
        var binding = new object();

        registry.Register([new CloneTrackerContributionFactory()], [new CloneTrackerInstance()]);
        viewModel.RefreshTrackerEditors(new Dictionary<TrackerKey, IDictionary<int, object?>?>
        {
            [previous.Key] = new Dictionary<int, object?> { [42] = binding },
        });

        var refreshed = (CloneTrackerExtension)viewModel.Extensions.Single();
        Assert.AreNotSame(previous, refreshed);
        Assert.AreEqual(1, refreshed.BatchLoadCallCount);
        Assert.AreSame(binding, refreshed.LastBatchBinding);
        Assert.IsTrue(refreshed.OptionsLoadedWhenCloned);
        Assert.AreEqual("ISSUE-42", refreshed.Selection);
        Assert.AreEqual(1, previous.CloneCallCount);
    }

    [TestMethod]
    public void ImportedExtraFieldsAreReadOnlyInDialog()
    {
        var field = new WorkItemExtraField
        {
            FieldId = "field-id",
            FieldKey = "legacy.value",
            TagId = 7,
            TagName = "旧标签",
            Label = "旧值",
            Type = TagExtraFieldType.Text,
            Value = "历史值",
        };
        var viewModel = new WorkItemExtraFieldsViewModel(null!, 42, [field], isReadOnly: true);

        Assert.IsTrue(viewModel.IsReadOnly);
        Assert.AreEqual("查看附加信息", viewModel.Title);
        Assert.IsTrue(viewModel.Groups.Single().Fields.Single().IsReadOnly);
        Assert.IsFalse(viewModel.SaveCommand.CanExecute(null));
    }

    [TestMethod]
    public void SyncFromBatchUsesExplicitBatchLoadForMissingTrackerBinding()
    {
        var registry = CreateCloneTrackerRegistry();
        var viewModel = CreateViewModel(registry);
        var item = new WorkItem
        {
            Id = 42,
            CreateDate = "2026-08-24",
            Comment = "批量加载",
        };
        LoadExistingItem(viewModel, item);
        var extension = (CloneTrackerExtension)viewModel.Extensions.Single();
        var bindings = new Dictionary<TrackerKey, IDictionary<int, object?>?>
        {
            [extension.Key] = new Dictionary<int, object?>(),
        };

        viewModel.SyncFromBatch(
            [],
            [],
            bindings,
            new Dictionary<int, ICollection<WorkItemExtraField>>());

        Assert.AreEqual(1, extension.BatchLoadCallCount);
        Assert.AreEqual(0, extension.RegularLoadCallCount);
        Assert.IsNull(extension.LastBatchBinding);
    }

    [TestMethod]
    public void PristineNewItemDoesNotNeedPersistenceBeforeReplacement()
    {
        var viewModel = CreateViewModel();

        Assert.IsFalse(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void EditedNewItemNeedsPersistenceBeforeReplacement()
    {
        var title = CreateViewModel();
        title.Comment = "新建后输入标题";
        var note = CreateViewModel();
        note.Note = "备注";
        var time = CreateViewModel();
        time.Time = 0.25;
        var priority = CreateViewModel();
        priority.Priority = WorkPriorities.P1;

        Assert.IsTrue(title.ShouldPersistBeforeReplacement);
        Assert.IsTrue(note.ShouldPersistBeforeReplacement);
        Assert.IsTrue(time.ShouldPersistBeforeReplacement);
        Assert.IsTrue(priority.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void UnifiedTimeInputParsesExpressionsAndNormalizesSameValue()
    {
        var viewModel = CreateViewModel();
        viewModel.Time = 1.5;
        viewModel.TimeInput = "90m";

        viewModel.ApplyTimeInputCommand.Execute(null);

        Assert.AreEqual(1.5, viewModel.Time, 0.0001);
        Assert.AreEqual("1.5", viewModel.TimeInput);
    }

    [TestMethod]
    public void UnifiedTimeInputSynchronizesQuickValuesAndCanResetEdits()
    {
        var viewModel = CreateViewModel();

        viewModel.QuickTimeCommand.Execute("30m");

        Assert.AreEqual(0.5, viewModel.Time, 0.0001);
        Assert.AreEqual("0.5", viewModel.TimeInput);

        viewModel.QuickTimeCommand.Execute("8h");
        Assert.AreEqual(8.0, viewModel.Time, 0.0001);
        Assert.AreEqual("8", viewModel.TimeInput);

        viewModel.TimeInput = "尚未应用";
        viewModel.ResetTimeInputCommand.Execute(null);

        Assert.AreEqual("8", viewModel.TimeInput);
    }

    [TestMethod]
    public void InvalidUnifiedTimeInputDoesNotOverwriteCommittedTime()
    {
        var viewModel = CreateViewModel();
        viewModel.Time = 2;
        viewModel.TimeInput = "invalid";

        viewModel.ApplyTimeInputCommand.Execute(null);

        Assert.AreEqual(2, viewModel.Time, 0.0001);
        Assert.AreEqual("invalid", viewModel.TimeInput);
    }
    [TestMethod]
    public void ApplyTemplateOverwritesFieldsAndReplacesAllTags()
    {
        var currentTag = new WorkTag { Id = 1, Name = "当前标签", Level = TagLevels.Primary };
        var templateTag = new WorkTag { Id = 2, Name = "模板标签", Level = TagLevels.Primary };
        var viewModel = CreateViewModel();
        viewModel.AllTags.Add(currentTag);
        viewModel.AllTags.Add(templateTag);
        viewModel.Comment = "当前标题";
        viewModel.Time = 2;
        viewModel.AddTags([currentTag], TagAddSource.User);
        var template = new Template
        {
            Name = "覆盖模板",
            DefaultTitle = "模板标题",
            DefaultTime = 1.5,
            DefaultWorkTags = [templateTag.Id],
        };

        viewModel.ApplyTemplate(template);

        Assert.AreEqual("模板标题", viewModel.Comment);
        Assert.AreEqual(1.5, viewModel.Time, 0.0001);
        CollectionAssert.AreEqual(new[] { templateTag }, viewModel.WorkTags.ToArray());

        viewModel.ApplyTemplate(new Template { Name = "空模板" });

        Assert.AreEqual(string.Empty, viewModel.Comment);
        Assert.AreEqual(0, viewModel.Time, 0.0001);
        Assert.IsEmpty(viewModel.WorkTags);
    }

    [TestMethod]
    public void UpdateFromTemplateOnlyFillsMissingFieldsAndRequiresNoExistingTags()
    {
        var currentTag = new WorkTag { Id = 1, Name = "当前标签", Level = TagLevels.Primary };
        var templateTag = new WorkTag { Id = 2, Name = "模板标签", Level = TagLevels.Primary };
        var template = new Template
        {
            Name = "更新模板",
            DefaultTitle = "模板标题",
            DefaultTime = 1.5,
            DefaultWorkTags = [templateTag.Id],
        };
        var filled = CreateViewModel();
        filled.AllTags.Add(currentTag);
        filled.AllTags.Add(templateTag);
        filled.Comment = "已有标题";
        filled.Time = 2;
        filled.AddTags([currentTag], TagAddSource.User);

        filled.UpdateFromTemplate(template);

        Assert.AreEqual("已有标题", filled.Comment);
        Assert.AreEqual(2, filled.Time, 0.0001);
        CollectionAssert.AreEqual(new[] { currentTag }, filled.WorkTags.ToArray());

        var empty = CreateViewModel();
        empty.AllTags.Add(templateTag);

        empty.UpdateFromTemplate(template);

        Assert.AreEqual("模板标题", empty.Comment);
        Assert.AreEqual(1.5, empty.Time, 0.0001);
        CollectionAssert.AreEqual(new[] { templateTag }, empty.WorkTags.ToArray());
    }

    [TestMethod]
    public void ApplyTemplateReplacesPersistedTagsInDatabase()
    {
        using var database = CreateDatabase();
        var currentTag = database.CreateWorkTag("当前标签", true, 0);
        var templateTag = database.CreateWorkTag("模板标签", true, 1);
        var item = database.CreateWorkItem("2026-08-25", "当前标题");
        Assert.IsTrue(database.WorkItemAddTag(item, currentTag));
        var viewModel = CreateViewModel(database: database);
        viewModel.AllTags.Add(currentTag);
        viewModel.AllTags.Add(templateTag);
        LoadExistingItem(viewModel, item);
        viewModel.WorkTags.Add(currentTag);
        var template = new Template
        {
            Name = "持久化模板",
            DefaultTitle = "替换标题",
            DefaultTime = 1,
            DefaultWorkTags = [templateTag.Id],
        };

        viewModel.ApplyTemplate(template);

        CollectionAssert.AreEqual(
            new[] { templateTag.Id },
            database.GetWorkItemTags(item).Select(tag => tag.Id).ToArray());
        CollectionAssert.AreEqual(new[] { templateTag }, viewModel.WorkTags.ToArray());
    }
    [TestMethod]
    public void LockedItemIgnoresTemplateOperations()
    {
        var currentTag = new WorkTag { Id = 1, Name = "当前标签", Level = TagLevels.Primary };
        var templateTag = new WorkTag { Id = 2, Name = "模板标签", Level = TagLevels.Primary };
        var viewModel = CreateViewModel();
        viewModel.AllTags.Add(currentTag);
        viewModel.AllTags.Add(templateTag);
        LoadExistingItem(viewModel, new WorkItem
        {
            Id = 42,
            CreateDate = "2026-08-25",
            Comment = "锁定标题",
            Time = 2,
            IsReadOnly = true,
        });
        viewModel.WorkTags.Add(currentTag);
        var template = new Template
        {
            Name = "锁定模板",
            DefaultTitle = "模板标题",
            DefaultTime = 1,
            DefaultWorkTags = [templateTag.Id],
        };

        viewModel.ApplyTemplate(template);
        viewModel.UpdateFromTemplate(template);

        Assert.AreEqual("锁定标题", viewModel.Comment);
        Assert.AreEqual(2, viewModel.Time, 0.0001);
        CollectionAssert.AreEqual(new[] { currentTag }, viewModel.WorkTags.ToArray());
    }



    [TestMethod]
    public void DeleteFailureKeepsPersistedWorkItemInEditor()
    {
        using var database = CreateDatabase();
        var item = database.CreateWorkItem("2026-08-22", "待删除");
        var viewModel = CreateViewModel(database: database);
        SetWorkItem(viewModel, item);
        Assert.IsTrue(database.DeleteWorkItem(item));

        Assert.IsFalse(viewModel.Delete());
        Assert.AreSame(item, GetWorkItem(viewModel));
        Assert.IsTrue(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void RemoveTagFailureKeepsTagInEditor()
    {
        using var database = CreateDatabase();
        var item = database.CreateWorkItem("2026-08-22", "只读记录");
        var tag = database.CreateWorkTag("保留标签", false, 1);
        Assert.IsTrue(database.WorkItemAddTag(item, tag));
        Assert.IsTrue(database.MarkWorkItemReadOnly(item));
        item.IsReadOnly = true;

        var viewModel = CreateViewModel(database: database);
        SetWorkItem(viewModel, item);
        viewModel.WorkTags.Add(tag);

        viewModel.DelTagCommand.Execute(tag);

        CollectionAssert.Contains(viewModel.WorkTags, tag);
        Assert.IsTrue(database.GetWorkItemTags(item).Any(persisted => persisted.Id == tag.Id));
    }

    [TestMethod]
    public void ImportedReadOnlyItemRejectsAddingTags()
    {
        using var database = CreateDatabase();
        var item = database.CreateWorkItem("2026-08-26", "导入记录");
        var tag = database.CreateWorkTag("不可添加", true, 0);
        Assert.IsTrue(database.MarkWorkItemReadOnly(item));
        var viewModel = CreateViewModel(database: database);
        LoadExistingItem(viewModel, item);

        viewModel.AddTags([tag], TagAddSource.User);

        Assert.IsFalse(viewModel.CanEditTags);
        Assert.AreEqual(0, viewModel.WorkTags.Count);
        Assert.AreEqual(0, database.GetWorkItemTags(item).Count);
    }

    [TestMethod]
    public void EditableWorkTagRenamePersistsToDatabase()
    {
        using var database = CreateDatabase();
        var tag = database.CreateWorkTag("旧名称", true, 1);
        var editable = new EditableWorkTag(tag, database) { Name = "新名称" };

        Assert.IsTrue(editable.ApplyChanges(out var error), error);
        Assert.AreEqual("新名称", tag.Name);
        Assert.AreEqual("新名称", database.AllWorkTags().Single().Name);
    }

    [TestMethod]
    public void EditableWorkTagDuplicateNameFailureKeepsOriginalModel()
    {
        using var database = CreateDatabase();
        var tag = database.CreateWorkTag("原名称", true, 1);
        database.CreateWorkTag("已存在", false, 2);
        var editable = new EditableWorkTag(tag, database) { Name = "已存在" };

        Assert.IsFalse(editable.ApplyChanges(out var error));
        StringAssert.Contains(error, "重复");
        Assert.AreEqual("原名称", tag.Name);
        Assert.AreEqual("原名称", database.AllWorkTags().Single(item => item.Id == tag.Id).Name);
    }

    [TestMethod]
    public void UnchangedExistingItemDoesNotNeedPersistenceBeforeReplacement()
    {
        var viewModel = CreateViewModel();
        LoadExistingItem(viewModel, new WorkItem
        {
            Id = 7,
            CreateDate = "2026-08-21",
            Comment = "已保存事项",
            Time = 1.5,
            Priority = WorkPriorities.P1,
        });

        Assert.IsFalse(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void ChangedExistingItemNeedsPersistenceBeforeReplacement()
    {
        var viewModel = CreateViewModel();
        LoadExistingItem(viewModel, new WorkItem
        {
            Id = 7,
            CreateDate = "2026-08-21",
            Comment = "已保存事项",
        }, "原备注");

        viewModel.Note = "修改后的备注";

        Assert.IsTrue(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void ImportedReadOnlyItemDoesNotNeedPersistenceBeforeReplacement()
    {
        var viewModel = CreateViewModel();
        SetWorkItem(viewModel, new WorkItem
        {
            Id = 8,
            CreateDate = "2026-08-22",
            Comment = "迁移记录",
            IsReadOnly = true,
        });

        Assert.IsFalse(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void ImportedReadOnlyItemDisablesTrackerEditorContent()
    {
        var viewModel = CreateViewModel(CreateCloneTrackerRegistry());
        LoadExistingItem(viewModel, new WorkItem
        {
            Id = 8,
            CreateDate = "2026-08-22",
            Comment = "迁移记录",
            IsReadOnly = true,
        });

        viewModel.SyncFromBatch(
            [],
            [],
            null,
            new Dictionary<int, ICollection<WorkItemExtraField>>
            {
                [8] = [new WorkItemExtraField { FieldId = "unexpected", Label = "不应显示" }],
            });

        Assert.IsTrue(viewModel.IsImportedReadOnly);
        Assert.IsTrue(viewModel.IsLocked);
        Assert.IsFalse(viewModel.CanEditTags);
        Assert.IsTrue(viewModel.HasExtraFields);
        Assert.IsFalse(viewModel.ShowExtraFieldsButton);
        Assert.IsFalse(viewModel.CanOpenExtraFields);
        Assert.IsTrue(viewModel.IsExtraFieldsReadOnly);
        Assert.IsTrue(viewModel.TrackerTabs.Single().IsHostReadOnly);
    }

    [TestMethod]
    public void SynchronizedItemAllowsEditingTagsAndExtraFields()
    {
        var viewModel = CreateViewModel(CreateCloneTrackerRegistry());
        var extension = (CloneTrackerExtension)viewModel.Extensions.Single();
        extension.IsLocked = true;
        LoadExistingItem(viewModel, new WorkItem
        {
            Id = 10,
            CreateDate = "2026-08-22",
            Comment = "已同步记录",
        });

        viewModel.SyncFromBatch(
            [],
            [],
            null,
            new Dictionary<int, ICollection<WorkItemExtraField>>
            {
                [10] = [new WorkItemExtraField { FieldId = "synced", Label = "同步字段" }],
            });

        Assert.IsTrue(viewModel.IsLocked);
        Assert.IsTrue(viewModel.CanEditTags);
        Assert.IsTrue(viewModel.ShowExtraFieldsButton);
        Assert.IsTrue(viewModel.CanOpenExtraFields);
        Assert.IsFalse(viewModel.IsExtraFieldsReadOnly);
        Assert.AreEqual("附加信息", viewModel.ExtraFieldsButtonText);
    }

    [TestMethod]
    public void SynchronizedItemCanAddAndRemoveTagsWhileGenericFieldsStayLocked()
    {
        using var database = CreateDatabase();
        var item = database.CreateWorkItem("2026-08-26", "已同步记录");
        var tag = database.CreateWorkTag("本地标签", true, 0);
        var viewModel = CreateViewModel(CreateCloneTrackerRegistry(), database);
        var extension = (CloneTrackerExtension)viewModel.Extensions.Single();
        extension.IsLocked = true;
        LoadExistingItem(viewModel, item);

        viewModel.AddTags([tag], TagAddSource.User);

        Assert.IsTrue(viewModel.IsLocked);
        Assert.IsTrue(viewModel.CanEditTags);
        CollectionAssert.Contains(viewModel.WorkTags, tag);
        Assert.IsTrue(database.GetWorkItemTags(item).Any(persisted => persisted.Id == tag.Id));

        viewModel.DelTagCommand.Execute(tag);

        CollectionAssert.DoesNotContain(viewModel.WorkTags, tag);
        Assert.IsFalse(database.GetWorkItemTags(item).Any(persisted => persisted.Id == tag.Id));
    }

    [TestMethod]
    public void EditableItemKeepsTrackerEditorContentEnabled()
    {
        var viewModel = CreateViewModel(CreateCloneTrackerRegistry());
        LoadExistingItem(viewModel, new WorkItem
        {
            Id = 9,
            CreateDate = "2026-08-22",
            Comment = "普通记录",
        });

        viewModel.SyncFromBatch(
            [],
            [],
            null,
            new Dictionary<int, ICollection<WorkItemExtraField>>
            {
                [9] = [new WorkItemExtraField { FieldId = "normal", Label = "正常字段" }],
            });

        Assert.IsFalse(viewModel.IsImportedReadOnly);
        Assert.IsTrue(viewModel.ShowExtraFieldsButton);
        Assert.IsTrue(viewModel.CanOpenExtraFields);
        Assert.IsFalse(viewModel.IsExtraFieldsReadOnly);
        Assert.AreEqual("附加信息", viewModel.ExtraFieldsButtonText);
        Assert.IsFalse(viewModel.TrackerTabs.Single().IsHostReadOnly);
    }

    [TestMethod]
    public void CloneWithoutTrackerBindingsInitializesOptionsAndSkipsSelection()
    {
        var viewModel = CreateViewModel(CreateCloneTrackerRegistry());
        var source = (CloneTrackerExtension)viewModel.Extensions.Single();
        source.Load(null);
        source.Selection = "ISSUE-1";

        var clone = viewModel.Clone(includeTrackerBindings: false);
        var target = (CloneTrackerExtension)clone.Extensions.Single();

        Assert.IsTrue(target.OptionsLoaded);
        Assert.IsNull(target.Selection);
        Assert.AreEqual(0, source.CloneCallCount);
    }

    [TestMethod]
    public void CloneWithTrackerBindingsInitializesTargetBeforeCopyingSelection()
    {
        var viewModel = CreateViewModel(CreateCloneTrackerRegistry());
        var source = (CloneTrackerExtension)viewModel.Extensions.Single();
        source.Load(null);
        source.Selection = "ISSUE-1";

        var clone = viewModel.Clone();
        var target = (CloneTrackerExtension)clone.Extensions.Single();

        Assert.IsTrue(target.OptionsLoaded);
        Assert.IsTrue(target.OptionsLoadedWhenCloned);
        Assert.AreEqual("ISSUE-1", target.Selection);
        Assert.AreEqual(1, source.CloneCallCount);
    }

    [TestMethod]
    public void NewItemAddedTagInitializesDefaultAndKeepsExplicitClear()
    {
        using var database = CreateDatabase();
        var tag = database.CreateWorkTag("默认字段", true, 0);
        var definition = new TagExtraFieldDefinition
        {
            FieldKey = "new.default",
            TagId = tag.Id,
            Label = "默认内容",
            Type = TagExtraFieldType.Text,
            DefaultValue = "预填内容",
        };
        Assert.IsTrue(database.CreateTagExtraFieldDefinition(definition));
        var viewModel = CreateViewModel(database: database);

        viewModel.AddTags([tag], TagAddSource.User);

        Assert.AreEqual("预填内容", viewModel.ExtraFieldValues.Single().Value);
        Assert.AreEqual("预填内容", viewModel.GetExtraFieldsSnapshot().Single().Value);

        viewModel.ExtraFieldValues.Single().Value = string.Empty;
        viewModel.AddTags([tag], TagAddSource.User);

        Assert.AreEqual(string.Empty, viewModel.GetExtraFieldsSnapshot().Single().Value);
    }

    [TestMethod]
    public void ExistingItemAddedTagAppliesOnlyNewTagDefaults()
    {
        using var database = CreateDatabase();
        var oldTag = database.CreateWorkTag("旧标签", true, 0);
        var newTag = database.CreateWorkTag("新标签", false, 0);
        var oldDefinition = new TagExtraFieldDefinition
        {
            FieldKey = "old.default",
            TagId = oldTag.Id,
            Label = "旧默认值",
            Type = TagExtraFieldType.Text,
            DefaultValue = "不应回填",
        };
        var newDefinition = new TagExtraFieldDefinition
        {
            FieldKey = "new.persisted-default",
            TagId = newTag.Id,
            Label = "新默认值",
            Type = TagExtraFieldType.Text,
            DefaultValue = "应当预填",
        };
        Assert.IsTrue(database.CreateTagExtraFieldDefinition(oldDefinition));
        Assert.IsTrue(database.CreateTagExtraFieldDefinition(newDefinition));
        var item = database.CreateWorkItem("2026-08-25", "已有事项");
        Assert.IsTrue(database.WorkItemAddTag(item, oldTag));
        var viewModel = CreateViewModel(database: database);
        LoadExistingItem(viewModel, item);
        viewModel.SyncAll();

        viewModel.AddTags([newTag], TagAddSource.User);

        var fields = database.GetWorkItemExtraFields(item).ToDictionary(field => field.FieldId);
        Assert.AreEqual(string.Empty, fields[oldDefinition.FieldId].Value);
        Assert.AreEqual("应当预填", fields[newDefinition.FieldId].Value);
    }

    [TestMethod]
    public void CloneSourceValueOverridesInitializedDefaultWithoutDuplicates()
    {
        using var database = CreateDatabase();
        var tag = database.CreateWorkTag("克隆标签", true, 0);
        var definition = new TagExtraFieldDefinition
        {
            FieldKey = "clone.default",
            TagId = tag.Id,
            Label = "克隆字段",
            Type = TagExtraFieldType.Text,
            DefaultValue = "默认值",
        };
        Assert.IsTrue(database.CreateTagExtraFieldDefinition(definition));
        var item = database.CreateWorkItem("2026-08-25", "克隆来源");
        Assert.IsTrue(database.WorkItemAddTag(item, tag));
        Assert.IsTrue(database.SaveWorkItemExtraFieldValues(item.Id,
        [
            new WorkItemExtraFieldValue
            {
                WorkItemId = item.Id,
                FieldId = definition.FieldId,
                Value = "来源值",
            },
        ]));
        var viewModel = CreateViewModel(database: database);
        LoadExistingItem(viewModel, item);
        viewModel.SyncAll();

        var clone = viewModel.Clone();

        Assert.AreEqual(1, clone.ExtraFieldValues.Count);
        Assert.AreEqual("来源值", clone.ExtraFieldValues.Single().Value);
        Assert.AreEqual("来源值", clone.GetExtraFieldsSnapshot().Single().Value);
    }

    [TestMethod]
    public void TrackerUploadResultIncludesRecoveryDetails()
    {
        var attemptedAt = new DateTimeOffset(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(8));
        var result = new TrackerUploadResult(
            new TrackerKey("test", "local"),
            false,
            false,
            "网络连接中断",
            State: TrackerUploadState.Uncertain,
            AttemptedAt: attemptedAt);

        StringAssert.Contains(result.ResultSummary, "请先核对远程记录后再决定是否重试");
        var expectedAttemptedAt = attemptedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        StringAssert.Contains(result.ResultSummary, $"尝试时间：{expectedAttemptedAt}");
    }

    private static WorkEditorViewModel CreateViewModel(
        TrackerUiContributionRegistry? trackerRegistry = null,
        DbInterfaceBase? database = null)
        => new(
            new DbShareData(NullLogger<DbShareData>.Instance),
            new NoopPersistenceCoordinator(),
            new RecordingUploadCoordinator(),
            trackerRegistry ?? new TrackerUiContributionRegistry(),
            string.Empty,
            new NoopTagAutomationCoordinator(),
            database: database);

    private static SQLiteDb CreateDatabase()
    {
        var database = new SQLiteDb(new TestSqliteFactory());
        Assert.IsTrue(database.Connect());
        Assert.IsTrue(database.Initialized());
        var migration = database.MigrateTo(DataVersion.VersionCode, new DbMigrationOptions(CreateBackup: false));
        Assert.IsTrue(migration.Success, migration.Error);
        return database;
    }

    private static TrackerUiContributionRegistry CreateCloneTrackerRegistry()
    {
        var registry = new TrackerUiContributionRegistry();
        registry.Register([new CloneTrackerContributionFactory()], [new CloneTrackerInstance()]);
        return registry;
    }
    private static void SetWorkItem(WorkEditorViewModel viewModel, WorkItem item)
    {
        var property = typeof(WorkEditorViewModel).GetProperty(
            "WorkItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(property);
        property.SetValue(viewModel, item);
    }

    private static void LoadExistingItem(
        WorkEditorViewModel viewModel,
        WorkItem item,
        string note = "")
    {
        SetWorkItem(viewModel, item);
        viewModel.WorkId = item.Id;
        viewModel.Date = item.CreateDate;
        viewModel.Comment = item.Comment;
        viewModel.Time = item.Time;
        viewModel.Priority = item.Priority;
        viewModel.Note = note;
        viewModel.AcceptCurrentStateAsPersisted();
    }

    private static WorkItem? GetWorkItem(WorkEditorViewModel viewModel)
    {
        var property = typeof(WorkEditorViewModel).GetProperty(
            "WorkItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(property);
        return property.GetValue(viewModel) as WorkItem;
    }

    private sealed class CloneTrackerInstance : ITrackerInstance
    {
        public string PluginId => "test.clone";
        public string InstanceId => "local";
        public string DisplayName => "克隆测试";
        public string Icon => string.Empty;
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => null;
    }

    private sealed class CloneTrackerContributionFactory : ITrackerUiContributionFactory
    {
        public string PluginId => "test.clone";
        public ITrackerUiContribution Create(ITrackerInstance instance)
            => new CloneTrackerContribution(instance);
    }

    private sealed class CloneTrackerContribution(ITrackerInstance instance) : ITrackerUiContribution
    {
        public string PluginId => instance.PluginId;
        public ITrackerInstance Instance => instance;
        public ViewModelBase? CreateSettingsPage(object configuration) => null;
        public ViewModelBase? CreateManagementPage(string instanceId) => null;
        public ITrackerEditorExtension? CreateEditorExtension(string instanceId)
            => new CloneTrackerExtension();
    }

    private sealed class CloneTrackerExtension : ViewModelBase, ITrackerEditorExtension
    {
        public TrackerKey Key => new("test.clone", "local");
        public string InstanceId => Key.InstanceId;
        ViewModelBase ITrackerEditorExtension.View => this;
        public bool IsLocked { get; set; }
        public bool CanDelete => true;
        public bool OptionsLoaded { get; private set; }
        public bool OptionsLoadedWhenCloned { get; private set; }
        public string? Selection { get; set; }
        public int CloneCallCount { get; private set; }
        public int RegularLoadCallCount { get; private set; }
        public int BatchLoadCallCount { get; private set; }
        public object? LastBatchBinding { get; private set; }

        public void Load(WorkItem? item, object? binding = null)
        {
            RegularLoadCallCount++;
            OptionsLoaded = true;
        }

        public void LoadFromBatch(WorkItem? item, object? binding)
        {
            BatchLoadCallCount++;
            LastBatchBinding = binding;
            OptionsLoaded = true;
        }

        public bool Save(WorkItem item) => OptionsLoaded;

        public void CloneTo(ITrackerEditorExtension? target)
        {
            CloneCallCount++;
            if (target is not CloneTrackerExtension clone)
                return;
            clone.OptionsLoadedWhenCloned = clone.OptionsLoaded;
            clone.Selection = Selection;
        }

        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
            => Task.FromResult(new TrackerOperationResult(true));
    }

    private sealed class RecordingUploadCoordinator : ITrackerUploadCoordinator
    {
        public WorkItem? LastItem { get; private set; }

        public Task<WorkUploadResult> UploadAsync(
            WorkItem item,
            IReadOnlyCollection<ITrackerEditorExtension> extensions)
        {
            LastItem = item;
            return Task.FromResult(new WorkUploadResult([
                new TrackerUploadResult(
                    new TrackerKey("test", "local"),
                    true,
                    false,
                    State: TrackerUploadState.Succeeded),
            ]));
        }
    }

    private sealed class NoopPersistenceCoordinator : IWorkItemPersistenceCoordinator
    {
        public WorkItemSaveResult Save(DbInterfaceBase db, WorkItemSaveRequest request)
            => new(false, false, Error: "测试不应保存工作项");
    }

    private sealed class NoopTagAutomationCoordinator : ITagAutomationCoordinator
    {
        public TagAutomationResult TagAdded(
            WorkItem? item,
            WorkTag tag,
            TagAutomationContext context,
            IReadOnlyCollection<ITrackerEditorExtension> extensions)
            => new(Array.Empty<TagAutomationInstanceResult>());
    }

    private sealed class TestSqliteFactory : IDbFactory
    {
        private readonly Config _config = new() { FilePath = ":memory:" };

        public string Name => "SQLite";
        public bool Usable => true;
        public DbInterfaceBase Create() => new SQLiteDb(this);
        public Migration? GetMigration(uint version) => new SQLiteFactory().GetMigration(version);
        public object GetConfig() => _config;
    }

    private sealed class TestApplication : TestBaseApplication;
}
