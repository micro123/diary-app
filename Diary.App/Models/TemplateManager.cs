using System;
using System.Collections.Generic;
using Diary.Core.Configure;
using Diary.Core.Data.App;
using Diary.Core.Utils;
using Diary.Utils;

namespace Diary.App.Models;

[StorageFile("templates.json")]
public class TemplateManager: SingletonBase<TemplateManager>
{
    private TemplateManager()
    {
        EasySaveLoad.Load(this);
    }
    
    public ICollection<Template> Templates { get; set; } =  Array.Empty<Template>();
}