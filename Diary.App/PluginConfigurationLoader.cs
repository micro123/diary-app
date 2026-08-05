using Diary.Core.Utils;
using Diary.PluginBase;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections;

namespace Diary.App;

/// <summary>
/// 主程序统一创建并加载插件配置。配置格式和迁移细节由插件配置类型负责声明。
/// </summary>
public sealed class PluginConfigurationLoader
{
    public object Load(ITrackerPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var configuration = plugin.CreateConfiguration();
        if (!EasySaveLoad.LoadJson(configuration, out var rawJson))
            return configuration;

        var migrations = plugin.GetConfigurationMigrations().ToArray();
        var isPackage = rawJson["PluginId"]?.Type == JTokenType.String
            && rawJson["Payload"] is JObject;
        var schemaVersion = 0;
        var payload = rawJson;
        if (isPackage)
        {
            var packagePluginId = (string?)rawJson["PluginId"];
            if (!string.Equals(packagePluginId, plugin.Manifest.Id, StringComparison.Ordinal))
            {
                throw new PluginConfigurationMigrationException(
                    plugin.Manifest.Id,
                    0,
                    0,
                    $"插件配置归属错误：期望 {plugin.Manifest.Id}，实际 {packagePluginId}");
            }

            if (!int.TryParse((string?)rawJson["SchemaVersion"], out schemaVersion)
                || schemaVersion < 0)
            {
                throw new PluginConfigurationMigrationException(
                    plugin.Manifest.Id,
                    0,
                    0,
                    "插件配置 SchemaVersion 无效");
            }

            payload = ((JObject)rawJson["Payload"]!).DeepClone() as JObject
                ?? throw new PluginConfigurationMigrationException(
                    plugin.Manifest.Id,
                    schemaVersion,
                    schemaVersion,
                    "插件配置 Payload 必须是 JSON 对象");
        }

        var targetVersion = migrations.Length == 0
            ? schemaVersion
            : migrations.Max(migration => migration.ToVersion);
        var originalVersion = schemaVersion;
        var migrated = false;
        while (schemaVersion < targetVersion)
        {
            var migration = migrations
                .Where(item => item.FromVersion == schemaVersion)
                .OrderBy(item => item.ToVersion)
                .FirstOrDefault();
            if (migration is null)
            {
                throw new PluginConfigurationMigrationException(
                    plugin.Manifest.Id,
                    originalVersion,
                    targetVersion,
                    $"找不到配置迁移步骤：{schemaVersion} -> {targetVersion}");
            }

            if (!string.Equals(migration.PluginId, plugin.Manifest.Id, StringComparison.Ordinal)
                || migration.ToVersion <= migration.FromVersion)
            {
                throw new PluginConfigurationMigrationException(
                    plugin.Manifest.Id,
                    originalVersion,
                    targetVersion,
                    $"配置迁移步骤无效：{migration.PluginId} {migration.FromVersion} -> {migration.ToVersion}");
            }

            try
            {
                payload = ToJsonObject(migration.Migrate(payload), plugin.Manifest.Id, schemaVersion);
            }
            catch (PluginConfigurationMigrationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PluginConfigurationMigrationException(
                    plugin.Manifest.Id,
                    originalVersion,
                    targetVersion,
                    $"配置迁移失败：{schemaVersion} -> {migration.ToVersion}：{ex.Message}",
                    ex);
            }

            schemaVersion = migration.ToVersion;
            migrated = true;
        }

        if (schemaVersion > targetVersion)
        {
            throw new PluginConfigurationMigrationException(
                plugin.Manifest.Id,
                originalVersion,
                targetVersion,
                $"插件配置版本 {schemaVersion} 高于当前支持版本 {targetVersion}");
        }

        ClearSerializedCollections(payload, configuration);
        JsonConvert.PopulateObject(payload.ToString(Formatting.None), configuration);
        if (migrated || (isPackage && schemaVersion != originalVersion))
        {
            var package = new JObject
            {
                ["PluginId"] = plugin.Manifest.Id,
                ["SchemaVersion"] = schemaVersion,
                ["Payload"] = payload,
            };
            if (isPackage)
            {
                foreach (var property in rawJson.Properties())
                {
                    if (property.Name is "PluginId" or "SchemaVersion" or "Payload")
                        continue;
                    package[property.Name] = property.Value.DeepClone();
                }
            }
            if (!EasySaveLoad.SaveJson(configuration, package))
                throw new PluginConfigurationMigrationException(
                    plugin.Manifest.Id,
                    originalVersion,
                    schemaVersion,
                    "插件配置升级后保存失败");
        }

        return configuration;
    }

    private static void ClearSerializedCollections(JObject payload, object configuration)
    {
        foreach (var property in configuration.GetType().GetProperties())
        {
            if (property.GetValue(configuration) is not IList collection
                || !payload.Properties().Any(item => string.Equals(
                    item.Name, property.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            collection.Clear();
        }
    }

    private static JObject ToJsonObject(object value, string pluginId, int version)
    {
        if (value is JObject json)
            return json;

        try
        {
            return JObject.FromObject(value);
        }
        catch (Exception ex)
        {
            throw new PluginConfigurationMigrationException(
                pluginId,
                version,
                version,
                "配置迁移结果无法转换为 JSON 对象",
                ex);
        }
    }
}
