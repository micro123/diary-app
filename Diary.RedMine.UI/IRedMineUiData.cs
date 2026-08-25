using System.Collections.ObjectModel;
using Diary.RedMine.Models;

namespace Diary.RedMine.UI;

public interface IRedMineUiData
{
    ObservableCollection<RedMineIssueDisplay> RedMineIssues { get; }
    ObservableCollection<RedMineIssueDisplay> RedMineIssuesOpen { get; }
    ObservableCollection<RedMineActivity> RedMineActivities { get; }

    void InitLoad();
    void UpdateIssueStatus(int issueId, bool disabled);
}
