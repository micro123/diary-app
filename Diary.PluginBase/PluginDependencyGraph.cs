namespace Diary.PluginBase;

/// <summary>插件必选依赖图工具。可选依赖不参与启动顺序，也不会形成阻塞环。</summary>
public static class PluginDependencyGraph
{
    public static IReadOnlyList<string> GetRegistrationOrder(
        IEnumerable<PluginManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var byId = manifests
            .GroupBy(manifest => manifest.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var id in byId.Keys)
            Visit(id);

        return order;

        void Visit(string id)
        {
            if (!visited.Add(id))
                return;

            foreach (var dependency in byId[id].Dependencies.Where(d => !d.Optional))
            {
                if (byId.ContainsKey(dependency.PluginId))
                    Visit(dependency.PluginId);
            }

            order.Add(id);
        }
    }

    public static IReadOnlySet<string> FindCyclicPluginIds(
        IEnumerable<PluginManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var byId = manifests
            .GroupBy(manifest => manifest.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cyclic = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in byId.Keys)
            Visit(id);

        return cyclic;

        void Visit(string id)
        {
            if (state.TryGetValue(id, out var existing))
            {
                if (existing == VisitState.Active)
                {
                    var start = stack.LastIndexOf(id);
                    if (start >= 0)
                        cyclic.UnionWith(stack.Skip(start));
                }
                return;
            }

            state[id] = VisitState.Active;
            stack.Add(id);
            foreach (var dependency in byId[id].Dependencies.Where(d => !d.Optional))
            {
                if (byId.ContainsKey(dependency.PluginId))
                    Visit(dependency.PluginId);
            }

            stack.RemoveAt(stack.Count - 1);
            state[id] = VisitState.Completed;
        }
    }

    private enum VisitState
    {
        Active,
        Completed,
    }
}
