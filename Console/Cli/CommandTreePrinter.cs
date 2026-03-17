using Console.Rendering;

namespace Console.Cli;

internal static class CommandTreePrinter
{
    public static void Print(TextWriter output, CommandDef root, string? filter)
    {
        output.WriteLine(Ansi.White(root.Name));
        PrintChildren(output, root, prefix: "", filter);
    }

    private static void PrintChildren(TextWriter output, CommandDef cmd, string prefix, string? filter)
    {
        var children = cmd.EnumerateChildren().ToList();

        if (filter is not null)
            children = children.Where(c => HasMatch(c, filter)).ToList();

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var isLast = i == children.Count - 1;
            var connector = isLast ? "└── " : "├── ";
            var childPrefix = isLast ? "    " : "│   ";

            var baseName = filter is null
                ? Ansi.White(child.Name)
                : HighlightName(child.Name, filter);
            var name = child.IsDataPlane
                ? baseName + Ansi.LightRed("*")
                : baseName;

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
                    var styledSegment = filter is null
                        ? Ansi.Dim(lines[j])
                        : HighlightDesc(lines[j], filter);

                    if (j == 0)
                        output.WriteLine($"{linePrefix}{name}  {styledSegment}");
                    else
                        output.WriteLine($"{continuation}{styledSegment}");
                }
            }
            var childFilter = filter is not null && Matches(child, filter) ? null : filter;
            PrintChildren(output, child, prefix + childPrefix, childFilter);
        }
    }

    private static bool Matches(CommandDef cmd, string filter) =>
        cmd.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (
            !string.IsNullOrWhiteSpace(cmd.Description)
            && cmd.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
        )
        || cmd.Aliases.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static bool HasMatch(CommandDef cmd, string filter) =>
        Matches(cmd, filter) || cmd.EnumerateChildren().Any(c => HasMatch(c, filter));

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
