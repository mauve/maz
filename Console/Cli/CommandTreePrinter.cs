using System.CommandLine;
using System.CommandLine.Invocation;
using Console.Rendering;

namespace Console.Cli;

internal static class CommandTreePrinter
{
    public static void Print(TextWriter output, Command root, string? filter)
    {
        output.WriteLine(Ansi.White(root.Name));
        PrintChildren(output, root, prefix: "", filter);
    }

    private static void PrintChildren(TextWriter output, Command cmd, string prefix, string? filter)
    {
        var children = cmd.Subcommands.Where(c => !c.Hidden).ToList();

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
            var name = DataPlaneRegistry.IsDataPlane(child)
                ? baseName + Ansi.LightRed("*")
                : baseName;

            var linePrefix = $"{prefix}{connector}";
            var descIndent = Ansi.VisibleLength(linePrefix) + Ansi.VisibleLength(name) + 2; // +2 for "  " separator
            // Keep tree-line characters from prefix; for non-last items place │ at the connector
            // column so the vertical line continues down to the next sibling
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
            PrintChildren(output, child, prefix + childPrefix, filter);
        }
    }

    private static bool Matches(Command cmd, string filter) =>
        cmd.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (
            !string.IsNullOrWhiteSpace(cmd.Description)
            && cmd.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
        )
        || cmd.Aliases.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static bool HasMatch(Command cmd, string filter) =>
        Matches(cmd, filter) || cmd.Subcommands.Any(c => HasMatch(c, filter));

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

    public static void PrintFlat(TextWriter output, Command root, string? filter)
    {
        PrintFlatCommand(output, root, root.Name, filter);
    }

    private static void PrintFlatCommand(
        TextWriter output,
        Command cmd,
        string path,
        string? filter
    )
    {
        if (filter is null || path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            output.WriteLine(path);
        foreach (var sub in cmd.Subcommands.Where(c => !c.Hidden))
            PrintFlatCommand(output, sub, $"{path} {sub.Name}", filter);
    }
}

internal sealed class CommandTreeAction(Command root, Option<string?> option)
    : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        var filter = parseResult.GetValue(option);
        CommandTreePrinter.Print(System.Console.Out, root, filter);
        return 0;
    }
}

internal sealed class CommandFlatAction(Command root, Option<string?> option)
    : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        var filter = parseResult.GetValue(option);
        CommandTreePrinter.PrintFlat(System.Console.Out, root, filter);
        return 0;
    }
}
