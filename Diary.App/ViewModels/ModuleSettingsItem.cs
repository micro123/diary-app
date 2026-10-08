using Diary.GUIBase.ViewModels;

namespace Diary.App.ViewModels;

public sealed class ModuleSettingsItem(
    string title,
    string moduleId,
    ViewModelBase viewModel) : SettingItemModel(title, $"可选模块设置：{moduleId}")
{
    public string ModuleId { get; } = moduleId;

    public ViewModelBase ViewModel { get; } = viewModel;
}
