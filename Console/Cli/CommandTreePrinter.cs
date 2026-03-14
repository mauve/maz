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

            var desc = string.IsNullOrWhiteSpace(child.Description)
                ? ""
                : "  " + (filter is null
                    ? Ansi.Dim(child.Description)
                    : HighlightDesc(child.Description, filter));

            output.WriteLine($"{prefix}{connector}{name}{desc}");
            PrintChildren(output, child, prefix + childPrefix, filter);
        }
    }

    private static bool Matches(Command cmd, string filter) =>
        cmd.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(cmd.Description)
            && cmd.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
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
}

internal sealed class CommandTreeAction(Command root, Option<string?> option) : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        var filter = parseResult.GetValue(option);
        CommandTreePrinter.Print(System.Console.Out, root, filter);
        return 0;
    }
}
