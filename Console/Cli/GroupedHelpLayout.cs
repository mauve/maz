using System.CommandLine;
using System.CommandLine.Help;

namespace Console.Cli;

internal static class GroupedHelpLayout
{
    public static IEnumerable<Func<HelpContext, bool>> Create(HelpContext _)
    {
        yield return WithStyledHeader(HelpBuilder.Default.SynopsisSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandUsageSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandArgumentsSection());
        yield return ctx => WriteGroupedOptions(ctx, showAdvanced: false);
        yield return WithStyledHeader(HelpBuilder.Default.SubcommandsSection());
        yield return WithStyledHeader(HelpBuilder.Default.AdditionalArgumentsSection());
        yield return RemarksSection;
    }

    public static IEnumerable<Func<HelpContext, bool>> CreateWithAdvanced(HelpContext _)
    {
        yield return WithStyledHeader(HelpBuilder.Default.SynopsisSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandUsageSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandArgumentsSection());
        yield return ctx => WriteGroupedOptions(ctx, showAdvanced: true);
        yield return WithStyledHeader(HelpBuilder.Default.SubcommandsSection());
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
            if (!section(inner)) return false;

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
            .Select(o => ctx.HelpBuilder.GetTwoColumnRow(o, ctx))
            .Select(r => new TwoColumnHelpRow(
                r.FirstColumnText,
                Ansi.StyleOptionDescription(r.SecondColumnText)
            ))
            .ToList();
        ctx.HelpBuilder.WriteColumns(rows, ctx);
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
