using System.Reflection;
using System.Diagnostics;

namespace Diary.Database;

internal static class DbExtensionFactoryLoader
{
    private static readonly Lazy<IReadOnlyList<IDbExtensionFactory>> Loaded = new(Load);

    public static IReadOnlyList<IDbExtensionFactory> Factories => Loaded.Value;

    private static IReadOnlyList<IDbExtensionFactory> Load()
        => LoadFromDirectory(AppContext.BaseDirectory, ReportFailure);

    internal static IReadOnlyList<IDbExtensionFactory> LoadFromDirectory(
        string directory,
        Action<string, Exception>? reportFailure = null)
    {
        var result = new List<IDbExtensionFactory>();
        foreach (var path in Directory.EnumerateFiles(directory, "Diary.*.dll"))
        {
            Type[] types;
            try
            {
                var assembly = Assembly.LoadFrom(path);
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                reportFailure?.Invoke(path, exception);
                foreach (var loaderException in exception.LoaderExceptions.OfType<Exception>())
                    reportFailure?.Invoke(path, loaderException);
                types = exception.Types.OfType<Type>().ToArray();
            }
            catch (Exception exception)
            {
                reportFailure?.Invoke(path, exception);
                continue;
            }

            foreach (var type in types)
            {
                if (!typeof(IDbExtensionFactory).IsAssignableFrom(type)
                    || type.IsAbstract
                    || type.IsInterface)
                    continue;
                try
                {
                    if (Activator.CreateInstance(type) is IDbExtensionFactory factory)
                        result.Add(factory);
                }
                catch (Exception exception)
                {
                    reportFailure?.Invoke($"{path}::{type.FullName}", exception);
                }
            }
        }

        return result;
    }

    private static void ReportFailure(string source, Exception exception)
        => Trace.TraceWarning(
            "Database extension load failed for {0}: {1}: {2}",
            source,
            exception.GetType().Name,
            exception.Message);
}
