using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Console.Cli;

internal static class RemarksRegistry
{
    private static readonly ConditionalWeakTable<Command, string> _remarks = new();

    internal static void Register(Command cmd, string text) => _remarks.AddOrUpdate(cmd, text);

    internal static string? Get(Command cmd) =>
        _remarks.TryGetValue(cmd, out var text) ? text : null;
}
