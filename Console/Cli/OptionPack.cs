using Console.Cli.Parsing;

namespace Console.Cli;

public abstract partial class OptionPack
{
    /// <summary>The heading shown for this pack's options section in help output. Empty = no group.</summary>
    public virtual string HelpTitle => "";

    /// <summary>Optional description shown beneath the section heading.</summary>
    public virtual string HelpSectionDescription => "";

    // ── New enumeration methods ────────────────────────────────────────

    /// <summary>Yields this pack's own options. Overridden by the generator.</summary>
    internal virtual IEnumerable<CliOption> EnumerateOptions()
    {
        yield break;
    }

    /// <summary>Yields child OptionPacks. Overridden by the generator.</summary>
    internal virtual IEnumerable<OptionPack> EnumerateChildPacks()
    {
        yield break;
    }

    /// <summary>Yields this pack's manually-added options. Overridden by hand-written packs.</summary>
    internal virtual IEnumerable<CliOption> EnumerateManualOptions()
    {
        yield break;
    }

    /// <summary>
    /// Yields all options from this pack and its children, tagged with group info.
    /// </summary>
    internal IEnumerable<CliOption> EnumerateAllOptions()
    {
        // Child packs first
        foreach (var child in EnumerateChildPacks())
        {
            foreach (var opt in child.EnumerateAllOptions())
                yield return opt;
        }

        // Own generated options
        foreach (var opt in EnumerateOptions())
        {
            TagOption(opt);
            yield return opt;
        }

        // Manual options
        foreach (var opt in EnumerateManualOptions())
        {
            TagOption(opt);
            yield return opt;
        }
    }

    private void TagOption(CliOption opt)
    {
        if (!string.IsNullOrEmpty(HelpTitle) && opt.HelpGroup is null)
            opt.HelpGroup = new OptionGroupInfo(HelpTitle, HelpSectionDescription);
    }

    // ── Value access ───────────────────────────────────────────────────
    // These must remain instance members because generated code calls this.GetValue(...).
#pragma warning disable CA1822
    protected bool HasParseResult => true; // Always true in new path (values live on CliOption)

    protected T GetValue<T>(CliOption<T> option) => option.Value!;
#pragma warning restore CA1822
}
