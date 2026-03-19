using Console.Rendering;

namespace Console.Cli;

internal static class CommandTreePrinter
{
    public static void Print(
        TextWriter output,
        CommandDef root,
        string? filter,
        CommandTab tab = CommandTab.All,
        CommandFilterMode filterMode = CommandFilterMode.NameOnly
    )
    {
        output.WriteLine(Ansi.White(root.Name));
        PrintChildren(output, root, prefix: "", filter, tab, filterMode, pathSegments: [root.Name]);
    }

    private static void PrintChildren(
        TextWriter output,
        CommandDef cmd,
        string prefix,
        string? filter,
        CommandTab tab,
        CommandFilterMode filterMode,
        List<string> pathSegments
    )
    {
        var children = cmd.EnumerateChildren().ToList();

        if (tab != CommandTab.All)
            children = children.Where(c => HasTabMatch(c, tab)).ToList();

        string[]? fuzzyTokens = null;
        if (filter is not null)
        {
            var tokens = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 1)
            {
                fuzzyTokens = tokens;
                children = children.Where(c => HasFuzzyMatch(c, tokens, pathSegments)).ToList();
            }
            else
            {
                children = children.Where(c => HasMatch(c, filter, filterMode)).ToList();
            }
        }

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var isLast = i == children.Count - 1;
            var connector = isLast ? "└── " : "├── ";
            var childPrefix = isLast ? "    " : "│   ";

            var childPath = new List<string>(pathSegments) { child.Name };

            string baseName;
            if (fuzzyTokens is not null)
                baseName = HighlightFuzzyName(child.Name, childPath, fuzzyTokens);
            else if (filter is not null)
                baseName = HighlightName(child.Name, filter);
            else
                baseName = Ansi.White(child.Name);

            var name = child.IsDataPlane ? baseName + " \u26a1" : baseName;
            if (child.IsManualCommand)
                name += " \u2728";

            var linePrefix = $"{prefix}{connector}";
            var descIndent = Ansi.VisibleLength(linePrefix) + Ansi.VisibleLength(name) + 2;
            var continuationConnector = isLast
                ? new string(' ', Ansi.VisibleLength(connector))
                : "│" + new string(' ', Ansi.VisibleLength(connector) - 1);
            var continuation =
                prefix + continuationConnector + new string(' ', Ansi.VisibleLength(name) + 2);

            if (string.IsNullOrWhiteSpace(child.Description))
            {
                output.WriteLine($"{linePrefix}{name}");
            }
            else
            {
                var consoleWidth = DefinitionList.GetConsoleWidth();
                var descWidth = Math.Max(1, consoleWidth - descIndent);
                var lines = DefinitionList.WordWrap(child.Description, descWidth);

                for (var j = 0; j < lines.Count; j++)
                {
                    var styledSegment =
                        filter is not null && fuzzyTokens is null
                            ? HighlightDesc(lines[j], filter)
                            : Ansi.Dim(lines[j]);

                    if (j == 0)
                        output.WriteLine($"{linePrefix}{name}  {styledSegment}");
                    else
                        output.WriteLine($"{continuation}{styledSegment}");
                }
            }

            string? childFilter;
            if (fuzzyTokens is not null)
                childFilter = filter; // fuzzy path match always threads through
            else
                childFilter =
                    filter is not null && Matches(child, filter, filterMode) ? null : filter;

            PrintChildren(
                output,
                child,
                prefix + childPrefix,
                childFilter,
                tab,
                filterMode,
                childPath
            );
        }
    }

    private static bool Matches(CommandDef cmd, string filter, CommandFilterMode filterMode) =>
        cmd.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || cmd.Aliases.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase))
        || (
            filterMode == CommandFilterMode.NameAndDescription
            && !string.IsNullOrWhiteSpace(cmd.Description)
            && cmd.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
        );

    private static bool HasMatch(CommandDef cmd, string filter, CommandFilterMode filterMode) =>
        Matches(cmd, filter, filterMode)
        || cmd.EnumerateChildren().Any(c => HasMatch(c, filter, filterMode));

    private static bool TabPredicate(CommandDef cmd, CommandTab tab) =>
        tab switch
        {
            CommandTab.Manual => cmd.IsManualCommand,
            CommandTab.Service => !cmd.IsManualCommand,
            CommandTab.DataPlane => cmd.IsDataPlane,
            _ => true,
        };

    private static bool HasTabMatch(CommandDef cmd, CommandTab tab) =>
        TabPredicate(cmd, tab) || cmd.EnumerateChildren().Any(c => HasTabMatch(c, tab));

    private static bool FuzzyPathMatches(List<string> pathSegments, string[] tokens)
    {
        int ti = 0;
        for (int si = 0; si < pathSegments.Count && ti < tokens.Length; si++)
            if (pathSegments[si].StartsWith(tokens[ti], StringComparison.OrdinalIgnoreCase))
                ti++;
        return ti == tokens.Length;
    }

    /// <summary>
    /// Returns which token (if any) matched the given segment name when walking the path
    /// left-to-right against the token list. Returns null if this segment wasn't consumed.
    /// </summary>
    private static string? FindMatchingToken(
        List<string> fullPath,
        string[] tokens,
        string segmentName
    )
    {
        int ti = 0;
        for (int si = 0; si < fullPath.Count && ti < tokens.Length; si++)
        {
            if (fullPath[si].StartsWith(tokens[ti], StringComparison.OrdinalIgnoreCase))
            {
                if (fullPath[si] == segmentName)
                    return tokens[ti];
                ti++;
            }
        }
        return null;
    }

    private static bool HasFuzzyMatch(CommandDef cmd, string[] tokens, List<string> parentPath)
    {
        var path = new List<string>(parentPath) { cmd.Name };
        if (FuzzyPathMatches(path, tokens))
            return true;
        return cmd.EnumerateChildren().Any(c => HasFuzzyMatch(c, tokens, path));
    }

    private static string HighlightFuzzyName(
        string segmentName,
        List<string> fullPath,
        string[] tokens
    )
    {
        var matched = FindMatchingToken(fullPath, tokens, segmentName);
        if (matched is null)
            return Ansi.White(segmentName);

        // The matched token is a prefix of this segment name
        var matchLen = matched.Length;
        return Ansi.Yellow(segmentName[..matchLen]) + Ansi.White(segmentName[matchLen..]);
    }

    private static string HighlightName(string text, string filter)
    {
        var idx = text.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return Ansi.White(text);
        return Ansi.White(text[..idx])
            + Ansi.Yellow(text.Substring(idx, filter.Length))
            + Ansi.White(text[(idx + filter.Length)..]);
    }

    private static string HighlightDesc(string text, string filter)
    {
        var idx = text.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return Ansi.Dim(text);
        return Ansi.Dim(text[..idx])
            + Ansi.Yellow(text.Substring(idx, filter.Length))
            + Ansi.Dim(text[(idx + filter.Length)..]);
    }

    public static void PrintFlat(TextWriter output, CommandDef root, string? filter)
    {
        PrintFlatCommand(output, root, root.Name, filter);
    }

    private static void PrintFlatCommand(
        TextWriter output,
        CommandDef cmd,
        string path,
        string? filter
    )
    {
        if (filter is null || path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            output.WriteLine(path);
        foreach (var sub in cmd.EnumerateChildren())
            PrintFlatCommand(output, sub, $"{path} {sub.Name}", filter);
    }
}
