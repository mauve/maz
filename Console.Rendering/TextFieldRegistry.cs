using System.Collections.Concurrent;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Models;

namespace Console.Rendering;

public static class TextFieldRegistry
{
    private static readonly ConcurrentDictionary<Type, string[]> _visibleFields = new();
    private static readonly ConcurrentDictionary<Type, HashSet<string>> _hiddenFields = new();

    private static readonly HashSet<Type> HeuristicHiddenTypes =
    [
        typeof(ResourceIdentifier),
        typeof(ETag),
        typeof(SystemData),
        typeof(Uri),
    ];

    /// <summary>Register explicit visible-only fields for a type (whitelist; overrides heuristic).</summary>
    public static void RegisterVisibleFields<T>(params string[] fields)
    {
        _visibleFields[typeof(T)] = fields;
    }

    /// <summary>Register additional fields to hide for a type (added on top of heuristic).</summary>
    public static void RegisterHiddenFields<T>(params string[] fields)
    {
        _hiddenFields.AddOrUpdate(
            typeof(T),
            _ => new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase),
            (_, existing) =>
            {
                foreach (var f in fields)
                    existing.Add(f);
                return existing;
            }
        );
    }

    /// <summary>Returns: true = always show, false = always hide, null = use heuristic.</summary>
    public static bool? IsFieldVisible(Type type, string propertyName)
    {
        if (_visibleFields.TryGetValue(type, out var visible))
        {
            return Array.Exists(
                visible,
                f => string.Equals(f, propertyName, StringComparison.OrdinalIgnoreCase)
            );
        }

        if (_hiddenFields.TryGetValue(type, out var hidden) && hidden.Contains(propertyName))
            return false;

        return null;
    }

    /// <summary>Returns true if the given type should be hidden by the default heuristic.</summary>
    public static bool IsTypeHiddenByHeuristic(Type type)
    {
        if (HeuristicHiddenTypes.Contains(type))
            return true;
        var underlying = Nullable.GetUnderlyingType(type);
        return underlying != null && HeuristicHiddenTypes.Contains(underlying);
    }
}
