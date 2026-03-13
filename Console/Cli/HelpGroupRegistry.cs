using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Console.Cli;

internal sealed record OptionGroupInfo(string Title, string Description);

internal static class HelpGroupRegistry
{
    private static readonly ConditionalWeakTable<Option, OptionGroupInfo> _groups = [];

    internal static void Tag(Option option, string title, string description) =>
        _groups.AddOrUpdate(option, new OptionGroupInfo(title, description));

    internal static OptionGroupInfo? GetGroup(Option option) =>
        _groups.TryGetValue(option, out var info) ? info : null;
}
