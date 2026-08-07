using Diary.Core.Configure;
using Diary.Core.Data.App;
using Diary.Core.Utils;
using Diary.Utils;

namespace Diary.App.Models;

[StorageFile("templates.json")]
public class TemplateManager : SingletonBase<TemplateManager>
{
    private TemplateManager()
    {
        EasySaveLoad.Load(this);
        if (EnsureStableIds())
            EasySaveLoad.Save(this);
    }

    public ICollection<Template> Templates { get; set; } = Array.Empty<Template>();

    private bool EnsureStableIds()
    {
        var changed = false;
        var ids = new HashSet<Guid>();
        foreach (var template in Templates)
        {
            if (!Guid.TryParse(template.Id, out var id) || !ids.Add(id))
            {
                do id = Guid.NewGuid(); while (!ids.Add(id));
                template.Id = id.ToString("D");
                changed = true;
            }
            else
            {
                var normalized = id.ToString("D");
                if (!string.Equals(template.Id, normalized, StringComparison.Ordinal))
                {
                    template.Id = normalized;
                    changed = true;
                }
            }
        }
        return changed;
    }

}
