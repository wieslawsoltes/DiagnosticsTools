using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Avalonia.Diagnostics;

/// <summary>
/// Provides helpers related to <see cref="Type"/> inspection.
/// </summary>
public static class TypeExtensions
{
    private static readonly ConditionalWeakTable<Type, string> s_getTypeNameCache = new();

    public static string GetTypeName(this Type type)
    {
        if (!s_getTypeNameCache.TryGetValue(type, out var name))
        {
            name = type.Name;
            if (Nullable.GetUnderlyingType(type) is { } nullable)
            {
                name = nullable.Name + "?";
            }
            else if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var arguments = type.GetGenericArguments();
                name = definition.Name.Substring(0, definition.Name.IndexOf('`'));
                name = $"{name}<{string.Join(",", arguments.Select(GetTypeName))}>";
            }

            s_getTypeNameCache.Add(type, name);
        }

        return name;
    }
}

