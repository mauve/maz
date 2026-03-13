using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Console.Cli;

internal static class AdvancedOptionRegistry
{
    private static readonly ConditionalWeakTable<Option, object?> _advanced = [];

    internal static void Register(Option option) => _advanced.AddOrUpdate(option, null);

    internal static bool IsAdvanced(Option option) => _advanced.TryGetValue(option, out _);
}
