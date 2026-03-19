using Console.Cli.Parsing;
using Console.Rendering;

namespace Console.Cli;

internal static class GroupedHelpLayout
{
    /// <summary>Render help for a command to the given output.</summary>
    public static void Render(TextWriter output, CommandDef cmd, bool showAdvanced = false,
        List<CommandDef>? commandPath = null)
    {
        WriteUsageSection(output, cmd, commandPath);
        WriteArgumentsSection(output, cmd);
        WriteGroupedOptions(output, cmd, showAdvanced, commandPath);
        WriteSubcommandsSection(output, cmd, showDetailedDescriptions: showAdvanced);
        WriteDescriptionSection(output, cmd);
        WriteRemarksSection(output, cmd);
    }

    private static void WriteUsageSection(TextWriter output, CommandDef cmd,
        List<CommandDef>? commandPath)
    {
        var names = commandPath is not null
            ? commandPath.Select(c => c.Name).ToList()
            : [cmd.Name];
        var baseLine = string.Join(" ", names);

        var allOpts = commandPath is not null
            ? cmd.EnumerateAllOptionsWithRecursive(commandPath)
            : cmd.EnumerateAllOptions();
        bool hasOptions = allOpts.Any(o => !o.Hidden);
        bool hasSubcommands = cmd.EnumerateChildren().Any();

        output.WriteLine();
        output.WriteLine(Ansi.Header("Usage:"));

        if (hasOptions)
            output.WriteLine($"  {baseLine} [options]");
        if (hasSubcommands)
            output.WriteLine($"  {baseLine} [command]");
        if (!hasOptions && !hasSubcommands)
            output.WriteLine($"  {baseLine}");
    }

    private static void WriteArgumentsSection(TextWriter output, CommandDef cmd)
    {
        var args = cmd.EnumerateArguments().Where(a => !a.Hidden).ToList();
        if (args.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine(Ansi.Header("Arguments:"));

        var nameWidth = args.Max(a => a.Name.Length + 2);
        foreach (var arg in args)
        {
            var label = $"<{arg.Name}>";
            var padded = label.PadRight(nameWidth);
            var desc = arg.Description ?? "";
            output.WriteLine(
                string.IsNullOrEmpty(desc)
                    ? $"  {Ansi.White(label)}"
                    : $"  {Ansi.White(padded)}  {Ansi.Dim(desc)}"
            );
        }
    }

    private static void WriteGroupedOptions(TextWriter output, CommandDef cmd, bool showAdvanced,
        List<CommandDef>? commandPath)
    {
        var allOpts = commandPath is not null
            ? cmd.EnumerateAllOptionsWithRecursive(commandPath).ToList()
            : cmd.EnumerateAllOptions().ToList();

        var all = allOpts.Where(o => !o.Hidden).ToList();
        if (all.Count == 0)
            return;

        var visible = showAdvanced
            ? all
            : all.Where(o => !o.IsAdvanced).ToList();
        bool hasAdvanced = all.Any(o => o.IsAdvanced);

        // Partition into ungrouped and ordered groups.
        var ungrouped = new List<CliOption>();
        var groupList = new List<(OptionGroupInfo Info, List<CliOption> Options)>();
        var groupIndex = new Dictionary<OptionGroupInfo, int>();

        foreach (var opt in visible)
        {
            var info = opt.HelpGroup;
            if (info is null)
            {
                ungrouped.Add(opt);
            }
            else
            {
                if (!groupIndex.TryGetValue(info, out int idx))
                {
                    idx = groupList.Count;
                    groupIndex[info] = idx;
                    groupList.Add((info, new List<CliOption>()));
                }
                groupList[idx].Options.Add(opt);
            }
        }

        if (ungrouped.Count == 0 && groupList.Count == 0)
            return;

        if (ungrouped.Count > 0)
            WriteSection(output, "Options:", null, ungrouped);

        foreach (var (info, options) in groupList)
        {
            if (cmd.HiddenHelpGroups?.Contains(info.Title) == true)
                continue;
            WriteSection(output, info.Title + ":", info.Description, options);
        }

        if (!showAdvanced && hasAdvanced)
        {
            output.WriteLine();
            output.WriteLine(Ansi.Dim("  Use --help-more to show advanced options."));
        }
    }

    private static void WriteSection(
        TextWriter output,
        string heading,
        string? description,
        List<CliOption> options
    )
    {
        output.WriteLine();
        output.WriteLine(Ansi.Header(heading));
        if (!string.IsNullOrWhiteSpace(description))
            output.WriteLine($"  {Ansi.Dim(description)}");

        var rows = options
            .Select(o =>
            {
                var rawAliases = o.AllNames.ToList();
                var aliases = SplitAliasesWithValueHint(rawAliases, o.IsBool, o.AllowMultipleArgumentsPerToken);
                var meta = o.Metadata;
                var metadata = new List<string>();
                if (o.Required)
                    metadata.Add("[required]");
                if (meta?.DefaultText is { } d)
                    metadata.Add($"[default: {d}]");
                if (meta?.EnvVar is { } e)
                    metadata.Add($"[env: {e}]");
                if (meta?.AllowedValues is { } a)
                    metadata.Add($"[allowed: {a}]");
                var main = o.Description ?? "";
                return (aliases, main, metadata);
            })
            .ToList();

        const string descIndent = "          "; // 10 spaces
        const string metaIndent = "            "; // 12 spaces

        foreach (var (aliases, main, metadata) in rows)
        {
            foreach (var alias in aliases)
                output.WriteLine($"  {alias}");

            if (!string.IsNullOrEmpty(main))
                output.WriteLine($"{descIndent}{Ansi.Dim(Ansi.StyleOptionDescription(main))}");

            foreach (var meta in metadata)
                output.WriteLine($"{metaIndent}{Ansi.StyleOptionDescription(meta)}");
        }
    }

    private static List<string> SplitAliasesWithValueHint(
        List<string> rawAliases,
        bool isBool,
        bool isMultiValue
    )
    {
        if (isBool)
        {
            var noSet = rawAliases
                .Where(a => a.StartsWith("--no-", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var alias in rawAliases)
            {
                if (alias.StartsWith("--no-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (
                    alias.StartsWith("--", StringComparison.Ordinal)
                    && noSet.Contains("--no-" + alias[2..])
                )
                    result.Add(Ansi.White("--[no-]" + alias[2..]));
                else
                    result.Add(Ansi.White(alias));
            }
            if (result.Count > 0)
                result[^1] += " " + Ansi.Cyan("[true|false]");
            return result;
        }

        var aliases = rawAliases.Select(Ansi.White).ToList();
        if (aliases.Count > 0)
            aliases[^1] += " " + Ansi.Cyan(isMultiValue ? "[value...]" : "[value]");
        return aliases;
    }

    private static void WriteDescriptionSection(TextWriter output, CommandDef cmd)
    {
        var description = cmd.Description;
        if (string.IsNullOrWhiteSpace(description))
            return;
        output.WriteLine();
        output.WriteLine(Ansi.Header("Description:"));
        output.WriteLine($"  {description}");
    }

    private static void WriteRemarksSection(TextWriter output, CommandDef cmd)
    {
        var text = cmd.DetailedDescription;
        if (string.IsNullOrWhiteSpace(text))
            return;
        output.WriteLine();
        foreach (var line in text.Split('\n'))
        {
            var rendered = MarkdownTerminal.RenderLine(line.TrimEnd());
            output.WriteLine(rendered is not null ? $"  {rendered}" : "");
        }
    }

    private static void WriteSubcommandsSection(TextWriter output, CommandDef cmd,
        bool showDetailedDescriptions)
    {
        var commands = cmd.EnumerateChildren().ToList();
        if (commands.Count == 0)
            return;

        output.WriteLine();
        output.WriteLine(Ansi.Header("Commands:"));

        bool hasAnyDetailedDescriptions = false;

        var rows = commands
            .Select(command =>
            {
                var name = Ansi.White(command.Name);
                var displayName = command.IsDataPlane
                    ? name + " \u26a1"
                    : name;
                if (command.IsManualCommand)
                    displayName += " \u2728";
                var desc = command.Description ?? "";
                return (command, displayName, desc);
            })
            .ToList();

        var firstWidth = rows.Max(r => Ansi.VisibleLength(r.displayName));

        foreach (var row in rows)
        {
            var padding = new string(' ', firstWidth - Ansi.VisibleLength(row.displayName));
            output.WriteLine($"  {row.displayName}{padding}  {row.desc}");

            var detail = row.command.DetailedDescription;
            if (string.IsNullOrWhiteSpace(detail))
                continue;

            hasAnyDetailedDescriptions = true;

            if (!showDetailedDescriptions)
                continue;

            foreach (var line in detail.Split('\n'))
            {
                if (line.Length == 0)
                {
                    output.WriteLine();
                    continue;
                }

                output.WriteLine($"    {Ansi.Dim(line.TrimEnd())}");
            }
        }

        if (!showDetailedDescriptions && hasAnyDetailedDescriptions)
        {
            output.WriteLine();
            output.WriteLine(Ansi.Dim("  Use --help-more to show detailed descriptions."));
        }
    }
}
