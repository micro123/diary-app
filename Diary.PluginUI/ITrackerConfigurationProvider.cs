using Diary.GUIBase.ViewModels;

namespace Diary.PluginUI;

/// <summary>
/// tracker 配置提供者（文档 §12）。插件贡献配置对象、校验、设置页。
/// 主程序负责持久化、版本识别、启用状态显示；插件负责字段、默认值、校验、敏感数据处理。
/// </summary>
public interface ITrackerConfigurationProvider
{
    string PluginId { get; }

    object CreateDefaultConfiguration();
    bool Validate(object configuration, out string? error);
    ViewModelBase? CreateSettingsPage(object configuration);
}
