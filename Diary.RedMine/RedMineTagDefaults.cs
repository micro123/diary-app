namespace Diary.RedMine;

public sealed record RedMineTagDefaultsResult(int? ActivityId, int? IssueId);

public static class RedMineTagDefaults
{
    public static RedMineTagDefaultsResult Apply(
        IEnumerable<RedMineTagRule> rules,
        int tagId,
        int? currentActivityId,
        int? currentIssueId,
        IReadOnlySet<int> availableActivityIds,
        IReadOnlySet<int> availableIssueIds)
    {
        var activityId = currentActivityId;
        var issueId = currentIssueId;
        foreach (var rule in rules
                     .Where(rule => rule.Enabled && rule.TagId == tagId)
                     .OrderByDescending(rule => rule.Priority))
        {
            if (activityId is null
                && rule.ActivityId is int candidateActivity
                && availableActivityIds.Contains(candidateActivity))
            {
                activityId = candidateActivity;
            }
            if (issueId is null
                && rule.IssueId is int candidateIssue
                && availableIssueIds.Contains(candidateIssue))
            {
                issueId = candidateIssue;
            }
        }
        return new RedMineTagDefaultsResult(activityId, issueId);
    }
}
