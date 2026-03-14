using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Console.Cli;

/// <summary>
/// Tracks which <see cref="Command"/> instances represent data-plane operations.
/// Data-plane commands bypass ARM and act directly on resource endpoints
/// (e.g. Key Vault data APIs), so they are flagged with a visual marker in help output.
/// </summary>
internal static class DataPlaneRegistry
{
    private static readonly ConditionalWeakTable<Command, object?> _dataPlane = new();
    private static readonly object _sentinel = new();

    internal static void Register(Command cmd) => _dataPlane.AddOrUpdate(cmd, _sentinel);

    internal static bool IsDataPlane(Command cmd) => _dataPlane.TryGetValue(cmd, out _);
}
