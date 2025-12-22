using Diary.Core.Configure;
using Diary.Core.Constants;

namespace Diary.Core.Data.AppConfig;

public class WorkConfig
{
    [ConfigureText("默认事项名称", helpTip: "新建事项时，标题默认填入此文本。")]
    public string DefaultTaskTitle { get; set; } = "";

    [ConfigureReal("每天工作时长", 0, 24)] public double DefaultDailyTotalHours { get; set; } = 8.0;

    [ConfigureButton("标签管理", "编辑标签", CommandNames.EditWorkTags, "打开标签编辑对话框以编辑所有标签。")]
    private int EditWorkTags { get; set; } = 0;

    [ConfigureButton("模板管理", "编辑模板", CommandNames.EditWorkTemplates, "打开模板编辑对话框以编辑工作模板。")]
    private int EditWorkTemplates { get; set; } = 0;
}