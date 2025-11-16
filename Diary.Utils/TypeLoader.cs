using System.Reflection;

namespace Diary.Utils;

public static class TypeLoader
{
    public static T? LoadAssemblyAndGetInstance<T>(string assemblyPath)
    {
        T? result = default;
        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            var types = assembly.GetTypes()
                .Where(x => typeof(T).IsAssignableFrom(x) && x is { IsInterface: false, IsAbstract: false });
            foreach (var type in types)
            {
                result = (T?)Activator.CreateInstance(type);
                if (result != null)
                    break;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
        return result;
    }

    public static IEnumerable<T> GetImplementations<T>(string dir, string pattern)
    {
        var dlls = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
        if  (dlls.Length == 0)
        {
            yield break;
        }

        foreach (var dll in dlls)
        {
            var obj = LoadAssemblyAndGetInstance<T>(dll);
            if (obj != null)
                yield return obj;
        }
    }
}