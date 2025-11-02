using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Utils;

public static class DiCollectionExtensions
{
    public static IServiceCollection AddTypesFromAssembly(this IServiceCollection collection, Assembly assembly)
    {
        var types = assembly.GetTypes();
        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract)
                continue;
            
            var attrs = type.GetCustomAttributes<DiAutoRegisterAttribute>();
            foreach (var attr in attrs)
            {
                if (attr.Singleton)
                {
                    if (string.IsNullOrEmpty(attr.Key))
                        collection.AddSingleton(attr.ServiceType ?? type, type);
                    else
                        collection.AddKeyedSingleton(attr.ServiceType ?? type, attr.Key, type);
                }
                else
                {
                    if (string.IsNullOrEmpty(attr.Key))
                        collection.AddScoped(attr.ServiceType ?? type, type);
                    else
                        collection.AddKeyedScoped(attr.ServiceType ?? type, attr.Key, type);
                }
            }
        }
        return collection;
    }
}