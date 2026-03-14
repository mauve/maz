using System.CommandLine;
using System.CommandLine.Help;
using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Console.Rendering;

namespace Console.Cli;

internal static partial class GroupedHelpLayout
{
    public static IEnumerable<Func<HelpContext, bool>> Create(HelpContext _)
    {
        yield return WithStyledHeader(HelpBuilder.Default.SynopsisSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandUsageSection());
        yield return WithStyledHeader(HelpBuilder.Default.CommandArgumentsSection());
        yield return ctx => WriteGroupedOptions(ctx, showAdvanced: false);
        yield return ctx => WriteSubcommandsSection(ctx, showDetailedDescriptions: false);
        yield return WithStyledHeader(HelpBuilder.Default.AdditionalArgumentsSection());
        yield return DescriptionSection;
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
        yield return DescriptionSection;
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
                var row = ctx.HelpBuilder.GetTwoColumnRow(o, ctx);
                var (main, metadata) = SplitOptionDescriptionAndMetadata(o, row.SecondColumnText);
                var aliases = SplitAliasesWithValueHint(
                    row.FirstColumnText,
                    o is Option<bool>,
                    o.AllowMultipleArgumentsPerToken
                );
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
    /// Splits the first-column text into individual alias strings and appends
    /// the appropriate value indicator to the last alias.
    /// Bool options: --foo + --no-foo compacted to --[no-]foo [true|false].
    /// Multi-value options: last alias gets [value...].
    /// Single-value options: last alias gets [value].
    /// </summary>
    private static List<string> SplitAliasesWithValueHint(
        string firstColumnText,
        bool isBool,
        bool isMultiValue
    )
    {
        // Strip any type hint and (REQUIRED) suffix that System.CommandLine adds
        var match = TypeHintRegex().Match(firstColumnText);
        var withoutHint = match.Success ? firstColumnText[..match.Index] : firstColumnText;
        withoutHint = RequiredSuffixRegex().Replace(withoutHint, "");
        var rawAliases = withoutHint.Split(", ").ToList();

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

        // Non-bool: always add value indicator regardless of whether S.CommandLine added a hint
        var aliases = rawAliases.Select(Ansi.White).ToList();
        if (aliases.Count > 0)
            aliases[^1] += " " + Ansi.Cyan(isMultiValue ? "[value...]" : "[value]");
        return aliases;
    }

    [GeneratedRegex(@"\s*<[^>]+>$")]
    private static partial Regex TypeHintRegex();

    [GeneratedRegex(@"\s*\(REQUIRED\)", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredSuffixRegex();

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
                var row = ctx.HelpBuilder.GetTwoColumnRow(command, ctx);
                var displayName = DataPlaneRegistry.IsDataPlane(command)
                    ? row.FirstColumnText + Ansi.LightRed("*")
                    : row.FirstColumnText;
                return (command, displayName, row.SecondColumnText);
            })
            .ToList();

        var firstWidth = rows.Max(r => Ansi.VisibleLength(r.displayName));

        foreach (var row in rows)
        {
            var padding = new string(' ', firstWidth - Ansi.VisibleLength(row.displayName));
            ctx.Output.WriteLine(
                $"  {row.displayName}{padding}  {row.SecondColumnText}"
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

    private static (string main, List<string> metadata) SplitOptionDescriptionAndMetadata(
        Option option,
        string secondColumnText
    )
    {
        var metadata = new List<string>();
        var main = secondColumnText;

        foreach (Match match in MetadataTagRegex().Matches(secondColumnText))
        {
            var tag = match.Value;
            if (tag.StartsWith("[default:", StringComparison.OrdinalIgnoreCase))
            {
                var rewritten = RewriteEnumDefaultTag(option, tag);
                if (option.AllowMultipleArgumentsPerToken)
                    rewritten = NormalizeDefaultTagListSeparators(rewritten);
                metadata.Add(rewritten);
                main = main.Replace(tag, "", StringComparison.Ordinal);
                continue;
            }

            if (tag.StartsWith("[env:", StringComparison.OrdinalIgnoreCase))
            {
                metadata.Add(tag);
                main = main.Replace(tag, "", StringComparison.Ordinal);
                continue;
            }

            if (tag.StartsWith("[allowed:", StringComparison.OrdinalIgnoreCase))
            {
                metadata.Add(tag);
                main = main.Replace(tag, "", StringComparison.Ordinal);
            }
        }

        main = Regex.Replace(main, "\\s{2,}", " ").Trim();
        if (option.Required)
            metadata.Insert(0, "[required]");
        return (main, metadata);
    }

    private static string NormalizeDefaultTagListSeparators(string tag)
    {
        const string prefix = "[default:";
        if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return tag;

        var inner = tag[prefix.Length..].TrimStart();
        if (inner.EndsWith(']'))
            inner = inner[..^1].TrimEnd();

        var parts = inner.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? tag : $"[default: {string.Join(", ", parts)}]";
    }

    private static string RewriteEnumDefaultTag(Option option, string defaultTag)
    {
        var enumType = GetOptionEnumType(option);
        if (enumType is null)
            return defaultTag;

        const string prefix = "[default:";
        if (!defaultTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return defaultTag;

        var value = defaultTag.Substring(prefix.Length).Trim();
        value = value.EndsWith(']') ? value[..^1].TrimEnd() : value;

        var converted = value
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(v => MapEnumTokenToDescription(enumType, v))
            .ToArray();

        if (converted.Any(c => c is null))
            return defaultTag;

        return $"[default: {string.Join("|", converted!)}]";
    }

    private static Type? GetOptionEnumType(Option option)
    {
        for (Type? t = option.GetType(); t is not null; t = t.BaseType)
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(Option<>))
                continue;

            var arg = t.GetGenericArguments()[0];
            var underlying = Nullable.GetUnderlyingType(arg);
            var candidate = underlying ?? arg;
            if (candidate.IsEnum)
                return candidate;

            if (candidate.IsArray)
            {
                var element = candidate.GetElementType();
                if (element is not null)
                {
                    var unwrappedElement = Nullable.GetUnderlyingType(element) ?? element;
                    if (unwrappedElement.IsEnum)
                        return unwrappedElement;
                }
            }

            if (candidate.IsGenericType)
            {
                var def = candidate.GetGenericTypeDefinition();
                if (
                    def == typeof(List<>)
                    || def == typeof(IReadOnlyList<>)
                    || def == typeof(IReadOnlyCollection<>)
                    || def == typeof(IEnumerable<>)
                )
                {
                    var element = candidate.GetGenericArguments()[0];
                    var unwrappedElement = Nullable.GetUnderlyingType(element) ?? element;
                    if (unwrappedElement.IsEnum)
                        return unwrappedElement;
                }
            }

            return null;
        }

        return null;
    }

    private static string? MapEnumTokenToDescription(Type enumType, string token)
    {
        if (!Enum.TryParse(enumType, token, ignoreCase: true, out var parsed) || parsed is null)
            return null;

        var memberName = Enum.GetName(enumType, parsed);
        if (memberName is null)
            return null;

        var field = enumType.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
        var desc = field?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        return string.IsNullOrWhiteSpace(desc) ? token : desc;
    }

    [GeneratedRegex(@"\[(default|env|allowed): [^\]]+\]", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataTagRegex();

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
