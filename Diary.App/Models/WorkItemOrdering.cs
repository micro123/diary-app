using System.Collections.ObjectModel;
using Diary.Core.Data.Base;

namespace Diary.App.Models;

internal static class WorkItemOrdering
{
    public static IEnumerable<T> ByPriorityAndId<T>(
        IEnumerable<T> items,
        Func<T, WorkPriorities> prioritySelector,
        Func<T, int> idSelector)
        => items
            .OrderBy(prioritySelector)
            .ThenBy(idSelector);

    public static void SortByPriorityAndId<T>(
        ObservableCollection<T> items,
        Func<T, WorkPriorities> prioritySelector,
        Func<T, int> idSelector)
    {
        var ordered = ByPriorityAndId(items, prioritySelector, idSelector).ToArray();
        for (var targetIndex = 0; targetIndex < ordered.Length; ++targetIndex)
        {
            var currentIndex = items.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
                items.Move(currentIndex, targetIndex);
        }
    }
}
