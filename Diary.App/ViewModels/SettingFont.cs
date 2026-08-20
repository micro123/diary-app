using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.App.Fonts;
using Diary.Core.Data.AppConfig;
using Diary.GUIBase.ViewModels;
using Ursa.Controls;

namespace Diary.App.ViewModels;

public sealed partial class SettingFont : SettingItemModel
{
    private readonly ViewConfig _config;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemFont))]
    [NotifyPropertyChangedFor(nameof(IsFontFile))]
    private string _source = AppFontSource.BundledDefault;

    [ObservableProperty]
    private string _systemFontFamily = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontFileDirectory))]
    private string _fontFilePath = string.Empty;

    [ObservableProperty]
    private string _fontFileStatus = string.Empty;

    public SettingFont(string title, string helpTip, object configuration) : base(title, helpTip)
    {
        _config = configuration as ViewConfig
            ?? throw new ArgumentException("字体设置必须绑定到视图配置。", nameof(configuration));

        var families = FontManager.Current.SystemFonts
            .Select(font => font.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(_config.SystemFontFamily)
            && !families.Contains(_config.SystemFontFamily, StringComparer.OrdinalIgnoreCase))
        {
            families.Insert(0, _config.SystemFontFamily);
        }
        SystemFontFamilies = families;
    }

    public IReadOnlyList<string> SourceOptions => AppFontSource.Options;

    public IReadOnlyList<string> SystemFontFamilies { get; }

    public bool IsSystemFont => Source == AppFontSource.SystemFont;

    public bool IsFontFile => Source == AppFontSource.FontFile;

    public string FontFileDirectory => Path.GetDirectoryName(FontFilePath) ?? string.Empty;

    public UsePickerTypes PickerType => UsePickerTypes.OpenFile;

    protected override void LoadAction()
    {
        Source = AppFontSource.Options.Contains(_config.FontSource)
            ? _config.FontSource
            : AppFontSource.BundledDefault;
        SystemFontFamily = string.IsNullOrWhiteSpace(_config.SystemFontFamily)
            ? FontManager.Current.DefaultFontFamily.Name
            : _config.SystemFontFamily;
        FontFilePath = _config.FontFilePath;
        RefreshFontFileStatus();
    }

    protected override void SaveAction()
    {
        switch (Source)
        {
            case AppFontSource.BundledDefault:
            case AppFontSource.SystemDefault:
                break;
            case AppFontSource.SystemFont:
                var familyName = SystemFontFamilies.FirstOrDefault(
                    name => string.Equals(name, SystemFontFamily, StringComparison.OrdinalIgnoreCase));
                if (familyName is null)
                    throw new InvalidOperationException("请选择当前系统中可用的字体。");
                SystemFontFamily = familyName;
                break;
            case AppFontSource.FontFile:
                if (!AppFontConfiguration.TryInspectFontFile(FontFilePath, out _, out var error))
                    throw new InvalidOperationException(error);
                FontFilePath = Path.GetFullPath(FontFilePath);
                break;
            default:
                throw new InvalidOperationException("请选择有效的字体来源。");
        }

        _config.FontSource = Source;
        _config.SystemFontFamily = SystemFontFamily;
        _config.FontFilePath = FontFilePath;
    }

    partial void OnFontFilePathChanged(string value)
    {
        RefreshFontFileStatus();
    }

    private void RefreshFontFileStatus()
    {
        if (string.IsNullOrWhiteSpace(FontFilePath))
        {
            FontFileStatus = "请选择 .ttf 或 .otf 字体文件。";
            return;
        }

        FontFileStatus = AppFontConfiguration.TryInspectFontFile(FontFilePath, out var familyName, out var error)
            ? $"检测到字体族：{familyName}"
            : error;
    }
}
