using System;
using System.Collections.Generic;
using Diary.Core.Configure;
using Diary.Core.Utils;
using Diary.Utils;

namespace Diary.App.Models;

public record Template
{
    public string Name { get; set; } =  string.Empty;
    public string DefaultTitle { get; set; } =  string.Empty;
    public double DefaultTime { get; set; } =  0;
    public int DefaultActivity {get; set; } = 0;
    public int DefaultIssue { get; set; } = 0;
    public ICollection<int> DefaultWorkTags { get; set; } = Array.Empty<int>();
}

[StorageFile("templates.json")]
public class TemplateManager: SingletonBase<TemplateManager>
{
    private TemplateManager()
    {
        EasySaveLoad.Load(this);
    }
    
    public ICollection<Template> Templates { get; set; } =  Array.Empty<Template>();
}