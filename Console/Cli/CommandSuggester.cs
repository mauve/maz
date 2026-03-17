using Console.Cli.Parsing;
using Console.Rendering;

namespace Console.Cli;

internal static class CommandSuggester
{
    /// <summary>
    /// Returns the first unmatched non-option token from the parse result,
    /// or null if there is no unknown command situation.
    /// </summary>
    public static string? GetUnknownToken(CliParseResult result)
    {
        foreach (var token in result.UnmatchedTokens)
        {
            if (!token.StartsWith('-'))
                return token;
        }
        return null;
    }

    /// <summary>
    /// Full suggestion flow. Returns exit code (0 if re-invoked successfully, 1 otherwise),
    /// or -1 if there are no matches and the caller should fall through to default error handling.
    /// </summary>
    public static int TrySuggest(
        CliParseResult result,
        string[] originalArgs,
        bool interactive,
        TextWriter stderr,
        Func<string?> readLine,
        CommandDef rootDef
    )
    {
        var token = GetUnknownToken(result);
        if (token is null)
            return -1;

        var parentCommand = result.Command ?? rootDef;
        var matches = FuzzyCommandMatcher.FindMatches(parentCommand, token);

        if (matches.Count == 0)
            return -1;

        var rootName = rootDef.Name;

        if (interactive)
        {
            if (matches.Count == 1)
            {
                var suggestion = matches[0].Cmd.Name;
                var proposed =
                    rootName
                    + " "
                    + string.Join(" ", ReplaceFirst(originalArgs, token, suggestion));
                stderr.Write($"Did you mean '{proposed}'? [Y/n]: ");
                var response = readLine()?.Trim().ToLowerInvariant() ?? "";
                if (response == "" || response == "y" || response == "yes")
                    return ReinvokeWith(rootDef, originalArgs, token, suggestion);

                return 1;
            }
            else
            {
                stderr.WriteLine("Unknown command. Did you mean one of these?");
                for (var i = 0; i < matches.Count; i++)
                {
                    var proposed =
                        rootName
                        + " "
                        + string.Join(" ", ReplaceFirst(originalArgs, token, matches[i].Cmd.Name));
                    stderr.WriteLine($"  {i + 1}) {proposed}");
                }
                stderr.Write("Enter number (or press Enter to cancel): ");

                var response = readLine()?.Trim() ?? "";
                if (
                    int.TryParse(response, out var choice)
                    && choice >= 1
                    && choice <= matches.Count
                )
                    return ReinvokeWith(rootDef, originalArgs, token, matches[choice - 1].Cmd.Name);

                return 1;
            }
        }
        else
        {
            if (matches.Count == 1)
            {
                var proposed =
                    rootName
                    + " "
                    + string.Join(" ", ReplaceFirst(originalArgs, token, matches[0].Cmd.Name));
                stderr.WriteLine(Ansi.Yellow($"Did you mean: '{proposed}'?"));
            }
            else
            {
                var suggestions = string.Join(
                    ", ",
                    matches.Select(m =>
                        $"'{rootName} {string.Join(" ", ReplaceFirst(originalArgs, token, m.Cmd.Name))}'"
                    )
                );
                stderr.WriteLine(Ansi.Yellow($"Did you mean one of: {suggestions}?"));
            }
            return 1;
        }
    }

    private static int ReinvokeWith(
        CommandDef rootDef,
        string[] originalArgs,
        string badToken,
        string replacement
    )
    {
        var newArgs = ReplaceFirst(originalArgs, badToken, replacement);
        var result2 = CliParser.Parse(newArgs, rootDef);

        if (result2.Errors.Count > 0)
        {
            foreach (var error in result2.Errors)
                System.Console.Error.WriteLine(Ansi.Red(error));
            return 1;
        }

        return result2.Command!.InvokeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string[] ReplaceFirst(string[] args, string oldToken, string newToken)
    {
        var result = (string[])args.Clone();
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] == oldToken)
            {
                result[i] = newToken;
                break;
            }
        }
        return result;
    }
}
