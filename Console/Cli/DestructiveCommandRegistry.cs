using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Console.Cli;

/// <summary>
/// Tracks which <see cref="Command"/> instances represent destructive operations
/// (those backed by DELETE, PUT, or POST HTTP methods).
/// Used by Program.cs to attach the global <c>--require-confirmation</c> guard.
/// </summary>
internal static class DestructiveCommandRegistry
{
    private static readonly ConditionalWeakTable<Command, object?> _destructive = new();
    private static readonly object _sentinel = new();

    internal static void Register(Command cmd) => _destructive.AddOrUpdate(cmd, _sentinel);

    internal static bool IsDestructive(Command cmd) => _destructive.TryGetValue(cmd, out _);
}
