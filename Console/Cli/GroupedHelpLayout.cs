using System.CommandLine;
using System.CommandLine.Help;
using Console.Rendering;

namespace Console.Cli;

internal static class GroupedHelpLayout
{
    public static IEnumerable<Func<HelpContext, bool>> Create(HelpContext _)
    {
        yield return WithStyledHeader(HelpBuilder.Default.SynopsisSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandUsageSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandArgumentsSection());
        yield return ctx => WriteGroupedOptions(ctx, showAdvanced: false);
        yield return ctx => WriteSubcommandsSection(ctx, showDetailedDescriptions: false);
        yield return WithStyledHeader(HelpBuilder.Default.AdditionalArgumentsSection());
        yield return RemarksSection;
    }

    public static IEnumerable<Func<HelpContext, bool>> CreateWithAdvanced(HelpContext _)
    {
        yield return WithStyledHeader(HelpBuilder.Default.SynopsisSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandUsageSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandArgumentsSection());
        yield return ctx => WriteGroupedOptions(ctx, showAdvanced: true);
        yield return ctx => WriteSubcommandsSection(ctx, showDetailedDescriptions: true);
        yield return WithStyledHeader(HelpBuilder.Default.AdditionalArgumentsSection());
        yield return RemarksSection;
    }

    /// <summary>
    /// Wraps a default S.CL section so its first non-empty line (the header) is styled with
    /// <see cref="Ansi.Header"/>. Captures the section's output via a temporary HelpContext,
    /// then replays it with the header styled.
    /// </summary>
    private static Func<HelpContext, bool> WithStyledHeader(Func<HelpContext, bool> section)
    {
        return ctx =>
        {
            using var capture = new StringWriter();
            var inner = new HelpContext(ctx.HelpBuilder, ctx.Command, capture, ctx.ParseResult);
            if (!section(inner))
                return false;

            using var reader = new StringReader(capture.ToString());
            bool headerStyled = false;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!headerStyled && line.Length > 0)
                {
                    ctx.Output.WriteLine(Ansi.Header(line));
                    headerStyled = true;
                }
                else
                {
                    ctx.Output.WriteLine(line);
                }
            }
            return true;
        };
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
                var (main, metadata) = BuildDescriptionAndMetadata(o);
                var firstCol = BuildFirstColumn(o);
                bool isRequired = o.Required;
                if (isRequired)
                    main = (main.Length > 0 ? main + " " : "") + Ansi.LightRed("[required]");
                return (firstCol, main, metadata);
            })
            .ToList();

        var firstWidth = rows.Count == 0 ? 0 : rows.Max(r => Ansi.VisibleLength(r.firstCol));
        var metadataIndent = new string(' ', 2 + firstWidth + 4);
        foreach (var row in rows)
        {
            var padding = new string(' ', firstWidth - Ansi.VisibleLength(row.firstCol));
            ctx.Output.WriteLine(
                $"  {row.firstCol}{padding}  {Ansi.StyleOptionDescription(row.main)}"
            );

            foreach (var meta in row.metadata)
                ctx.Output.WriteLine($"{metadataIndent}{Ansi.StyleOptionDescription(meta)}");
        }
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
                var row = ctx.HelpBuilder.GetTwoColumnRow(command, ctx);
                return (command, row.FirstColumnText, row.SecondColumnText);
            })
            .ToList();

        var firstWidth = rows.Max(r => r.FirstColumnText.Length);

        foreach (var row in rows)
        {
            ctx.Output.WriteLine(
                $"  {row.FirstColumnText.PadRight(firstWidth)}  {row.SecondColumnText}"
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

    private static string BuildFirstColumn(Option option)
    {
        var aliases = option.Aliases.ToList();

        // Boolean compaction: --foo + --no-foo → --[no-]foo
        for (int i = aliases.Count - 1; i >= 0; i--)
        {
            var alias = aliases[i];
            if (!alias.StartsWith("--", StringComparison.Ordinal))
                continue;
            var noVariant = "--no-" + alias[2..];
            int j = aliases.IndexOf(noVariant);
            if (j >= 0)
            {
                aliases[i] = "--[no-]" + alias[2..];
                aliases.RemoveAt(j);
            }
        }

        // Prefix compaction: --foo + --foo-bar → --foo[-bar]
        // Only compact when both start with "--" and suffix starts with "-"
        for (int i = aliases.Count - 1; i >= 0; i--)
        {
            var a = aliases[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                continue;
            for (int j = aliases.Count - 1; j >= 0; j--)
            {
                if (i == j) continue;
                var b = aliases[j];
                if (!b.StartsWith("--", StringComparison.Ordinal))
                    continue;
                // b is strict prefix of a, suffix starts with "-"
                if (a.Length > b.Length && a.StartsWith(b, StringComparison.Ordinal) && a[b.Length] == '-')
                {
                    var suffix = a[b.Length..];
                    aliases[j] = b + "[" + suffix + "]";
                    aliases.RemoveAt(i);
                    break;
                }
            }
        }

        var placeholder = string.IsNullOrEmpty(option.HelpName) ? "" : $" <{option.HelpName}>";
        return string.Join(", ", aliases) + placeholder;
    }

    private static (string main, List<string> metadata) BuildDescriptionAndMetadata(Option option)
    {
        var main = option.Description ?? "";
        var metadata = new List<string>();
        var meta = OptionMetadataRegistry.Get(option);
        if (meta is not null)
        {
            if (meta.EnvVar is not null) metadata.Add($"[env: {meta.EnvVar}]");
            if (meta.AllowedValues is not null) metadata.Add($"[allowed: {meta.AllowedValues}]");
            if (meta.DefaultText is not null) metadata.Add($"[default: {meta.DefaultText}]");
        }
        return (main, metadata);
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
