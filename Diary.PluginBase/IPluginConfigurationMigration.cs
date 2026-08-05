namespace Diary.PluginBase;

/// <summary>插件配置 JSON schema 迁移步骤。</summary>
/// <remarks>
/// 宿主以 <c>JObject</c> 形式传入配置，但契约使用 object 以避免插件基础契约绑定具体 JSON 库。
/// 需要保留未知字段的迁移应在插件内将输入转换为 JSON 对象后原位修改并返回。
/// </remarks>
public interface IPluginConfigurationMigration
{
    string PluginId { get; }
    int FromVersion { get; }
    int ToVersion { get; }

    object Migrate(object configuration);
}
