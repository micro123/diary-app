using Diary.App.Services;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Db.SQLite;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;

namespace Diary.AppTests;

[TestClass]
public sealed class TagSharePackageServiceTests
{
    [TestMethod]
    public async Task ExportImport_RoundTripsTagDefinitionAndEnablesDisabledTag()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diary-tags-{Guid.NewGuid():N}.diarytags");
        try
        {
            using var source = CreateDatabase();
            var tag = source.CreateWorkTag(
                "项目A",
                primary: false,
                color: 0x123456,
                new Dictionary<string, string> { ["projectNumber"] = "A001" });
            tag.Disabled = true;
            Assert.IsTrue(source.UpdateWorkTag(tag));
            Assert.IsTrue(source.CreateTagExtraFieldDefinition(new TagExtraFieldDefinition
            {
                FieldId = Guid.NewGuid().ToString("D"),
                FieldKey = "project.stage",
                TagId = tag.Id,
                Label = "项目阶段",
                Type = TagExtraFieldType.Choice,
                SortOrder = 10,
                Options = ["开发", "测试"],
            }));

            var service = new TagSharePackageService();
            await service.ExportAsync(path, source, Array.Empty<ITagRuleEditorContribution>());
            var json = await File.ReadAllTextAsync(path);
            Assert.IsFalse(json.Contains("disabled", StringComparison.OrdinalIgnoreCase));

            using var target = CreateDatabase();
            var preview = await service.PreviewImportAsync(path, target);
            var result = service.Import(
                preview,
                target,
                preview.Items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal),
                new Dictionary<string, ITagRuleEditorContribution>());

            Assert.AreEqual(1, result.Created);
            var imported = target.AllWorkTags().Single();
            Assert.AreEqual("项目A", imported.Name);
            Assert.AreEqual(TagLevels.Secondary, imported.Level);
            Assert.AreEqual(0x123456, imported.Color);
            Assert.IsFalse(imported.Disabled);
            Assert.AreEqual("A001", imported.Metadata["projectNumber"]);
            var field = target.GetTagExtraFieldDefinitions(imported.Id, includeDisabled: true).Single();
            Assert.AreEqual("project.stage", field.FieldKey);
            Assert.AreEqual(TagExtraFieldType.Choice, field.Type);
            CollectionAssert.AreEqual(new[] { "开发", "测试" }, field.Options.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task PreviewImport_FieldOwnedByAnotherTag_IsConflict()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diary-tags-{Guid.NewGuid():N}.diarytags");
        try
        {
            using var source = CreateDatabase();
            var sourceTag = source.CreateWorkTag("来源标签", true, 1);
            Assert.IsTrue(source.CreateTagExtraFieldDefinition(new TagExtraFieldDefinition
            {
                FieldId = Guid.NewGuid().ToString("D"),
                FieldKey = "shared.key",
                TagId = sourceTag.Id,
                Label = "共享字段",
                Type = TagExtraFieldType.Text,
            }));
            var service = new TagSharePackageService();
            await service.ExportAsync(path, source, Array.Empty<ITagRuleEditorContribution>());

            using var target = CreateDatabase();
            var otherTag = target.CreateWorkTag("其他标签", true, 2);
            Assert.IsTrue(target.CreateTagExtraFieldDefinition(new TagExtraFieldDefinition
            {
                FieldId = Guid.NewGuid().ToString("D"),
                FieldKey = "shared.key",
                TagId = otherTag.Id,
                Label = "本地字段",
                Type = TagExtraFieldType.Text,
            }));

            var preview = await service.PreviewImportAsync(path, target);
            Assert.IsTrue(preview.Items.Single().HasConflict);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task TrackerDescriptor_ExportsOnlyTypeAndName_AndInvalidRuleIsSkipped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diary-tags-{Guid.NewGuid():N}.diarytags");
        try
        {
            using var source = CreateDatabase();
            source.CreateWorkTag("项目A", true, 1);
            var exporter = new FakeTagRuleContribution(
                "tracker.redmine", "secret-instance-id", "公司 RedMine",
                TrackerTagRuleValidationState.Valid);
            var service = new TagSharePackageService();
            await service.ExportAsync(path, source, [exporter]);

            var json = await File.ReadAllTextAsync(path);
            StringAssert.Contains(json, "tracker.redmine");
            StringAssert.Contains(json, "公司 RedMine");
            Assert.IsFalse(json.Contains("secret-instance-id", StringComparison.Ordinal));

            using var target = CreateDatabase();
            var preview = await service.PreviewImportAsync(path, target);
            var importer = new FakeTagRuleContribution(
                "tracker.redmine", "local-instance", "本地 RedMine",
                TrackerTagRuleValidationState.Invalid);
            var result = service.Import(
                preview,
                target,
                preview.Items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal),
                new Dictionary<string, ITagRuleEditorContribution>
                {
                    [preview.Package.Trackers.Single().Key] = importer,
                });

            Assert.AreEqual(0, importer.ImportedRuleCount);
            Assert.AreEqual(1, result.Trackers.Single().Invalid);
            Assert.AreEqual(0, result.Trackers.Single().Imported);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SQLiteDb CreateDatabase()
    {
        var database = new SQLiteDb(new TestSqliteFactory());
        Assert.IsTrue(database.Connect());
        Assert.IsTrue(database.Initialized());
        return database;
    }

    private sealed class TestSqliteFactory : IDbFactory
    {
        private readonly Config _config = new() { FilePath = ":memory:" };
        public string Name => "SQLite";
        public bool Usable => true;
        public DbInterfaceBase Create() => new SQLiteDb(this);
        public Migration? GetMigration(uint version) => null;
        public object GetConfig() => _config;
    }

    private sealed class FakeTagRuleContribution(
        string pluginId,
        string instanceId,
        string instanceName,
        TrackerTagRuleValidationState validationState) : ITagRuleEditorContribution
    {
        public string PluginId => pluginId;
        public string InstanceId => instanceId;
        public string InstanceName => instanceName;
        public ViewModelBase View => null!;
        public int ImportedRuleCount { get; private set; }

        public void SelectTag(WorkTag tag)
        {
        }

        public IReadOnlyCollection<TrackerTagRulePackageItem> ExportRules(
            IReadOnlyDictionary<int, string> tagKeys)
            =>
            [
                new TrackerTagRulePackageItem(
                    tagKeys.Values.Single(),
                    new Dictionary<string, string?> { ["activityId"] = "999" }),
            ];

        public IReadOnlyCollection<TrackerTagRuleValidation> ValidateImportRules(
            IReadOnlyCollection<TrackerTagRulePackageItem> rules,
            IReadOnlyDictionary<string, int> tagIds)
            => rules.Select(rule => new TrackerTagRuleValidation(
                rule,
                validationState,
                validationState == TrackerTagRuleValidationState.Valid ? "有效" : "目标不存在"))
                .ToArray();

        public int ImportRules(
            IReadOnlyCollection<TrackerTagRulePackageItem> rules,
            IReadOnlyDictionary<string, int> tagIds)
        {
            ImportedRuleCount += rules.Count;
            return rules.Count;
        }

        public void Commit()
        {
        }

        public void Reload()
        {
        }
    }
}
