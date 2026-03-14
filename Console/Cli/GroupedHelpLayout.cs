using System.CommandLine;
using System.CommandLine.Help;
using Console.Rendering;

namespace Console.Cli;

internal static class GroupedHelpLayout
{
    public static IEnumerable<Func<HelpContext, bool>> Create(HelpContext _)
    {
        yield return WriteUsageSection;
        yield return WriteArgumentsSection;
        yield return ctx => WriteGroupedOptions(ctx, showAdvanced: false);
        yield return ctx => WriteSubcommandsSection(ctx, showDetailedDescriptions: false);
        yield return DescriptionSection;
        yield return RemarksSection;
    }

    public static IEnumerable<Func<HelpContext, bool>> CreateWithAdvanced(HelpContext _)
    {
        yield return WriteUsageSection;
        yield return WriteArgumentsSection;
        yield return ctx => WriteGroupedOptions(ctx, showAdvanced: true);
        yield return ctx => WriteSubcommandsSection(ctx, showDetailedDescriptions: true);
        yield return DescriptionSection;
        yield return RemarksSection;
    }

    private static bool WriteUsageSection(HelpContext ctx)
    {
        var cmd = ctx.Command;
        var ancestors = Ancestors(cmd).ToList();
        ancestors.Reverse();
        var baseLine = string.Join(" ", ancestors.Select(a => a.Name).Append(cmd.Name));

        bool hasOptions = AllOptions(cmd).Any(o => !o.Hidden);
        bool hasSubcommands = cmd.Subcommands.Any(c => !c.Hidden);

        ctx.Output.WriteLine();
        ctx.Output.WriteLine(Ansi.Header("Usage:"));

        if (hasOptions)
            ctx.Output.WriteLine($"  {baseLine} [options]");
        if (hasSubcommands)
            ctx.Output.WriteLine($"  {baseLine} [command]");
        if (!hasOptions && !hasSubcommands)
            ctx.Output.WriteLine($"  {baseLine}");

        return true;
    }

    private static bool WriteArgumentsSection(HelpContext ctx)
    {
        var args = ctx.Command.Arguments.Where(a => !a.Hidden && !string.IsNullOrEmpty(a.Name)).ToList();
        if (args.Count == 0)
            return false;

        ctx.Output.WriteLine();
        ctx.Output.WriteLine(Ansi.Header("Arguments:"));

        var nameWidth = args.Max(a => a.Name.Length + 2); // +2 for < >
        foreach (var arg in args)
        {
            var label = $"<{arg.Name}>";
            var padded = label.PadRight(nameWidth);
            var desc = arg.Description ?? "";
            ctx.Output.WriteLine(string.IsNullOrEmpty(desc)
                ? $"  {Ansi.White(label)}"
                : $"  {Ansi.White(padded)}  {Ansi.Dim(desc)}");
        }

        return true;
    }

    private static bool WriteGroupedOptions(HelpContext ctx, bool showAdvanced)
    {
        // Collect all non-hidden options: command-local first, then recursive from ancestors.
        var all = AllOptions(ctx.Command).Where(o => !o.Hidden).ToList();
        if (all.Count == 0)
            return false;

        var visible = showAdvanced
            ? all
            : all.Where(o => !AdvancedOptionRegistry.IsAdvanced(o)).ToList();
        bool hasAdvanced = all.Any(o => AdvancedOptionRegistry.IsAdvanced(o));

        // Partition into ungrouped (no OptionPack tag) and ordered groups.
        var ungrouped = new List<Option>();
        var groupList = new List<(OptionGroupInfo Info, List<Option> Options)>();
        var groupIndex = new Dictionary<OptionGroupInfo, int>();

        foreach (var opt in visible)
        {
            var info = HelpGroupRegistry.GetGroup(opt);
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
                    groupList.Add((info, new List<Option>()));
                }
                groupList[idx].Options.Add(opt);
            }
        }

        if (ungrouped.Count == 0 && groupList.Count == 0)
            return false;

        // Ungrouped (typically just --help)
        if (ungrouped.Count > 0)
            WriteSection(ctx, "Options:", null, ungrouped);

        foreach (var (info, options) in groupList)
        {
            if (!HiddenGroupRegistry.IsGroupHidden(ctx.Command, info.Title))
                WriteSection(ctx, info.Title + ":", info.Description, options);
        }

        if (!showAdvanced && hasAdvanced)
        {
            ctx.Output.WriteLine();
            ctx.Output.WriteLine(Ansi.Dim("  Use --help-more to show advanced options."));
        }

        return true;
    }

    private static void WriteSection(
        HelpContext ctx,
        string heading,
        string? description,
        List<Option> options
    )
    {
        ctx.Output.WriteLine();
        ctx.Output.WriteLine(Ansi.Header(heading));
        if (!string.IsNullOrWhiteSpace(description))
            ctx.Output.WriteLine($"  {Ansi.Dim(description)}");

        var rows = options
            .Select(o =>
            {
                var rawAliases = Enumerable.Concat([o.Name], o.Aliases).ToList();
                var aliases = SplitAliasesWithValueHint(rawAliases, o is Option<bool>, o.AllowMultipleArgumentsPerToken);
                var meta = OptionMetadataRegistry.Get(o);
                var metadata = new List<string>();
                if (o.Required) metadata.Add("[required]");
                if (meta?.DefaultText is { } d) metadata.Add($"[default: {d}]");
                if (meta?.EnvVar is { } e) metadata.Add($"[env: {e}]");
                if (meta?.AllowedValues is { } a) metadata.Add($"[allowed: {a}]");
                var main = o.Description ?? "";
                return (aliases, main, metadata);
            })
            .ToList();

        const string descIndent = "          "; // 10 spaces
        const string metaIndent = "            "; // 12 spaces

        foreach (var (aliases, main, metadata) in rows)
        {
            foreach (var alias in aliases)
                ctx.Output.WriteLine($"  {alias}");

            if (!string.IsNullOrEmpty(main))
                ctx.Output.WriteLine($"{descIndent}{Ansi.Dim(Ansi.StyleOptionDescription(main))}");

            foreach (var meta in metadata)
                ctx.Output.WriteLine($"{metaIndent}{Ansi.StyleOptionDescription(meta)}");
        }
    }

    /// <summary>
    /// Formats a list of raw aliases and appends the appropriate value indicator to the last alias.
    /// Bool options: --foo + --no-foo compacted to --[no-]foo [true|false].
    /// Multi-value options: last alias gets [value...].
    /// Single-value options: last alias gets [value].
    /// </summary>
    private static List<string> SplitAliasesWithValueHint(
        List<string> rawAliases,
        bool isBool,
        bool isMultiValue
    )
    {
        if (isBool)
        {
            // Compact: --foo + --no-foo → --[no-]foo [true|false]
            var noSet = rawAliases
                .Where(a => a.StartsWith("--no-", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var alias in rawAliases)
            {
                if (alias.StartsWith("--no-", StringComparison.OrdinalIgnoreCase))
                    continue; // emitted as part of the main alias below
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

        // Non-bool: always add value indicator
        var aliases = rawAliases.Select(Ansi.White).ToList();
        if (aliases.Count > 0)
            aliases[^1] += " " + Ansi.Cyan(isMultiValue ? "[value...]" : "[value]");
        return aliases;
    }

    private static bool DescriptionSection(HelpContext ctx)
    {
        var description = ctx.Command.Description;
        if (string.IsNullOrWhiteSpace(description))
            return false;
        ctx.Output.WriteLine();
        ctx.Output.WriteLine(Ansi.Header("Description:"));
        ctx.Output.WriteLine($"  {description}");
        return true;
    }

    private static bool RemarksSection(HelpContext ctx)
    {
        var text = RemarksRegistry.Get(ctx.Command);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        ctx.Output.WriteLine();
        foreach (var line in text.Split('\n'))
            ctx.Output.WriteLine(line.Length > 0 ? $"  {line.TrimEnd()}" : "");
        return true;
    }

    private static bool WriteSubcommandsSection(HelpContext ctx, bool showDetailedDescriptions)
    {
        var commands = ctx.Command.Subcommands.Where(c => !c.Hidden).ToList();
        if (commands.Count == 0)
            return false;

        ctx.Output.WriteLine();
        ctx.Output.WriteLine(Ansi.Header("Commands:"));

        bool hasAnyDetailedDescriptions = false;

        var rows = commands
            .Select(command =>
            {
                var name = Ansi.White(command.Name);
                var displayName = DataPlaneRegistry.IsDataPlane(command)
                    ? name + Ansi.LightRed("*")
                    : name;
                var desc = command.Description ?? "";
                return (command, displayName, desc);
            })
            .ToList();

        var firstWidth = rows.Max(r => Ansi.VisibleLength(r.displayName));

        foreach (var row in rows)
        {
            var padding = new string(' ', firstWidth - Ansi.VisibleLength(row.displayName));
            ctx.Output.WriteLine(
                $"  {row.displayName}{padding}  {row.desc}"
            );

            var detail = RemarksRegistry.Get(row.command);
            if (string.IsNullOrWhiteSpace(detail))
                continue;

            hasAnyDetailedDescriptions = true;

            if (!showDetailedDescriptions)
                continue;

            foreach (var line in detail.Split('\n'))
            {
                if (line.Length == 0)
                {
                    ctx.Output.WriteLine();
                    continue;
                }

                ctx.Output.WriteLine($"    {Ansi.Dim(line.TrimEnd())}");
            }
        }

        if (!showDetailedDescriptions && hasAnyDetailedDescriptions)
        {
            ctx.Output.WriteLine();
            ctx.Output.WriteLine(Ansi.Dim("  Use --help-more to show detailed descriptions."));
        }

        return true;
    }

    private static IEnumerable<Option> AllOptions(Command cmd)
    {
        foreach (var opt in cmd.Options)
            yield return opt;

        foreach (var ancestor in Ancestors(cmd))
        foreach (var opt in ancestor.Options)
            if (opt.Recursive)
                yield return opt;
    }

    private static IEnumerable<Command> Ancestors(Command cmd)
    {
        foreach (var parent in cmd.Parents.OfType<Command>())
        {
            yield return parent;
            foreach (var anc in Ancestors(parent))
                yield return anc;
        }
    }
}
