using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.ConfigurationTools;

namespace Diary.ConfigTool.ViewModels;

public sealed partial class ConfigurationItemViewModel(ConfigurationFileDescriptor descriptor) : ObservableObject
{
    public ConfigurationFileDescriptor Descriptor { get; } = descriptor;

    public string Id => Descriptor.Id;
    public string DisplayName => Descriptor.DisplayName;
    public string Category => Descriptor.Category;
    public string RelativePath => Descriptor.RelativePath;
    public bool IsEncrypted => Descriptor.IsEncrypted;

    [ObservableProperty]
    private string _status = "未检查";

    [ObservableProperty]
    private bool _exists;

    [ObservableProperty]
    private bool _hasBackup;

    public void Update(ConfigurationDocument document)
    {
        Exists = document.Status != ConfigurationDocumentStatus.Missing;
        HasBackup = document.HasBackup;
        Status = document.Status switch
        {
            ConfigurationDocumentStatus.Missing => "不存在",
            ConfigurationDocumentStatus.Loaded => document.Format,
            ConfigurationDocumentStatus.Unreadable => "无法读取",
            _ => "未知",
        };
    }
}
