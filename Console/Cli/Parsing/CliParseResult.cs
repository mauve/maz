namespace Console.Cli.Parsing;

/// <summary>
/// Result of parsing command-line arguments against a CommandDef tree.
/// Replaces System.CommandLine.ParseResult.
/// </summary>
public sealed class CliParseResult
{
    /// <summary>The matched leaf CommandDef (or nearest ancestor if no exact match).</summary>
    public CommandDef? Command { get; set; }

    /// <summary>The chain of CommandDefs from root to the matched command.</summary>
    public List<CommandDef> CommandPath { get; set; } = [];

    /// <summary>Parse errors (missing required options, unknown tokens, etc.).</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Tokens that could not be matched to any command or option.</summary>
    public List<string> UnmatchedTokens { get; } = [];

    /// <summary>Directives found in the input (e.g. [debug], [suggest:42]).</summary>
    public List<CliDirective> Directives { get; } = [];

    /// <summary>The raw arguments that were parsed.</summary>
    public string[] RawArgs { get; init; } = [];
}
