namespace Diary.RedMine;

public sealed record RedMineTagRuleWinner(
    string Field,
    string RuleId,
    int TargetId);

public sealed record RedMineTagRuleConflict(
    string Field,
    string WinningRuleId,
    IReadOnlyCollection<string> RuleIds);

public sealed record RedMineInvalidTagTarget(
    string Field,
    string RuleId,
    int TargetId);

public sealed record RedMineTagDefaultsResult(
    int? ActivityId,
    int? IssueId,
    IReadOnlyCollection<RedMineTagRuleWinner> Winners,
    IReadOnlyCollection<RedMineTagRuleConflict> Conflicts,
    IReadOnlyCollection<RedMineInvalidTagTarget> InvalidTargets);

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
        var matching = rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Enabled && item.Rule.TagId == tagId)
            .ToArray();
        var winners = new List<RedMineTagRuleWinner>();
        var conflicts = new List<RedMineTagRuleConflict>();
        var invalidTargets = new List<RedMineInvalidTagTarget>();
        var activityId = ResolveField(
            nameof(RedMineTagRule.ActivityId),
            currentActivityId,
            matching.Where(item => item.Rule.ActivityId is not null)
                .Select(item => (item.Rule, item.Index, item.Rule.ActivityId!.Value)),
            availableActivityIds,
            winners,
            conflicts,
            invalidTargets);
        var issueId = ResolveField(
            nameof(RedMineTagRule.IssueId),
            currentIssueId,
            matching.Where(item => item.Rule.IssueId is not null)
                .Select(item => (item.Rule, item.Index, item.Rule.IssueId!.Value)),
            availableIssueIds,
            winners,
            conflicts,
            invalidTargets);
        return new RedMineTagDefaultsResult(activityId, issueId, winners, conflicts, invalidTargets);
    }

    private static int? ResolveField(
        string field,
        int? currentValue,
        IEnumerable<(RedMineTagRule Rule, int Index, int TargetId)> candidates,
        IReadOnlySet<int> availableIds,
        ICollection<RedMineTagRuleWinner> winners,
        ICollection<RedMineTagRuleConflict> conflicts,
        ICollection<RedMineInvalidTagTarget> invalidTargets)
    {
        var ordered = candidates
            .OrderBy(item => item.Index)
            .ToArray();
        foreach (var candidate in ordered.Where(item => !availableIds.Contains(item.TargetId)))
            invalidTargets.Add(new RedMineInvalidTagTarget(field, candidate.Rule.RuleId, candidate.TargetId));

        var valid = ordered.Where(item => availableIds.Contains(item.TargetId)).ToArray();
        if (valid.Select(item => item.TargetId).Distinct().Skip(1).Any())
        {
            var first = valid[0];
            conflicts.Add(new RedMineTagRuleConflict(
                field,
                first.Rule.RuleId,
                valid.Select(item => item.Rule.RuleId).ToArray()));
        }

        if (currentValue is not null || valid.Length == 0)
            return currentValue;
        var winner = valid[0];
        winners.Add(new RedMineTagRuleWinner(
            field,
            winner.Rule.RuleId,
            winner.TargetId));
        return winner.TargetId;
    }
}
