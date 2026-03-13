using System.CommandLine;
using System.CommandLine.Help;

namespace Console.Cli;

internal static class GroupedHelpLayout
{
    public static IEnumerable<Func<HelpContext, bool>> Create(HelpContext _)
    {
        yield return HelpBuilder.Default.SynopsisSection();
        yield return HelpBuilder.Default.CommandUsageSection();
        yield return HelpBuilder.Default.CommandArgumentsSection();
        yield return WriteGroupedOptions;
        yield return HelpBuilder.Default.SubcommandsSection();
        yield return HelpBuilder.Default.AdditionalArgumentsSection();
    }

    private static bool WriteGroupedOptions(HelpContext ctx)
    {
        // Collect all visible options: command-local first, then recursive from ancestors.
        var all = AllOptions(ctx.Command).Where(o => !o.Hidden).ToList();
        if (all.Count == 0) return false;

        // Partition into ungrouped (no OptionPack tag) and ordered groups.
        var ungrouped = new List<Option>();
        var groupList = new List<(OptionGroupInfo Info, List<Option> Options)>();
        var groupIndex = new Dictionary<OptionGroupInfo, int>();

        foreach (var opt in all)
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

        // Ungrouped (typically just --help)
        if (ungrouped.Count > 0)
            WriteSection(ctx, "Options:", null, ungrouped);

        foreach (var (info, options) in groupList)
            WriteSection(ctx, info.Title + ":", info.Description, options);

        return true;
    }

    private static void WriteSection(HelpContext ctx, string heading, string? description, List<Option> options)
    {
        ctx.Output.WriteLine();
        ctx.Output.WriteLine(heading);
        if (!string.IsNullOrWhiteSpace(description))
            ctx.Output.WriteLine($"  {description}");
        var rows = options.Select(o => ctx.HelpBuilder.GetTwoColumnRow(o, ctx)).ToList();
        ctx.HelpBuilder.WriteColumns(rows, ctx);
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
