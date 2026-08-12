namespace Diary.App.Models;

public static class RecentPrimaryTagHistory
{
    public static IReadOnlyList<int> Merge(
        IEnumerable<int> preferredIds,
        IEnumerable<int> storedIds,
        int maximum = 8)
    {
        if (maximum <= 0)
            return Array.Empty<int>();

        return preferredIds
            .Concat(storedIds)
            .Where(id => id > 0)
            .Distinct()
            .Take(maximum)
            .ToArray();
    }
}
