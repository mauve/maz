namespace Console.Cli.Parsing;

/// <summary>
/// Represents a parsed directive from the command line (e.g. [debug], [suggest:42]).
/// Directives are bracketed tokens that appear before any command/option tokens.
/// </summary>
public sealed record CliDirective(string Name, string? Value = null)
{
    /// <summary>Try to parse a token as a directive. Returns null if not a directive.</summary>
    public static CliDirective? TryParse(string token)
    {
        if (token.Length < 3 || token[0] != '[' || token[^1] != ']')
            return null;

        var inner = token[1..^1];
        var colonIdx = inner.IndexOf(':');
        if (colonIdx >= 0)
            return new CliDirective(inner[..colonIdx], inner[(colonIdx + 1)..]);

        return new CliDirective(inner);
    }
}
