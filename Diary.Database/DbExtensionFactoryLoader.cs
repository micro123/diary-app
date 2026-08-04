using System.Reflection;

namespace Diary.Database;

internal static class DbExtensionFactoryLoader
{
    private static readonly Lazy<IReadOnlyList<IDbExtensionFactory>> Loaded = new(Load);

    public static IReadOnlyList<IDbExtensionFactory> Factories => Loaded.Value;

    private static IReadOnlyList<IDbExtensionFactory> Load()
    {
        var result = new List<IDbExtensionFactory>();
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Diary.RedMine.*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(path);
                foreach (var type in assembly.GetTypes())
                {
                    if (!typeof(IDbExtensionFactory).IsAssignableFrom(type)
                        || type.IsAbstract
                        || type.IsInterface)
                        continue;
                    if (Activator.CreateInstance(type) is IDbExtensionFactory factory)
                        result.Add(factory);
                }
            }
            catch
            {
                // A broken optional extension must not prevent the core database from starting.
            }
        }

        return result;
    }
}
