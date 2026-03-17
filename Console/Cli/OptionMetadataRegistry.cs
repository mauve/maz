namespace Console.Cli;

/// <summary>Metadata about a CLI option for display in help output.</summary>
public sealed record OptionMetadata(
    string? EnvVar,
    string? AllowedValues, // pre-joined: "json, table, column"
    string? DefaultText // pre-formatted: "column", "true", "cli|env"
);
