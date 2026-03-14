using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Console.Cli;

internal sealed record OptionMetadata(
    string? EnvVar,
    string? AllowedValues, // pre-joined: "json, table, column"
    string? DefaultText // pre-formatted: "column", "true", "cli|env"
);

internal static class OptionMetadataRegistry
{
    private static readonly ConditionalWeakTable<Option, OptionMetadata> _meta = [];

    internal static void Register(Option option, OptionMetadata metadata) =>
        _meta.AddOrUpdate(option, metadata);

    internal static OptionMetadata? Get(Option option) =>
        _meta.TryGetValue(option, out var m) ? m : null;
}
