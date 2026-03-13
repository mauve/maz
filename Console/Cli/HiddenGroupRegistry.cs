using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Console.Cli;

internal static class HiddenGroupRegistry
{
    private static readonly ConditionalWeakTable<Command, HashSet<string>> _hidden = new();

    internal static void HideGroup(Command cmd, string groupTitle)
    {
        var set = _hidden.GetOrCreateValue(cmd);
        set.Add(groupTitle);
    }

    internal static bool IsGroupHidden(Command cmd, string groupTitle) =>
        _hidden.TryGetValue(cmd, out var set) && set.Contains(groupTitle);
}
