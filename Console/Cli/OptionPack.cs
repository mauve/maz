// ParseResult.GetValue is annotated [MaybeNull] but callers handle nullability via option defaults
#pragma warning disable CS8603
using System.CommandLine;

namespace Console.Cli;

public interface IGlobalOption { }

/// <summary>Marker subclass — infrastructure adds this as a recursive (global) option.</summary>
public class GlobalOption<T> : Option<T>, IGlobalOption
{
    public GlobalOption(string name, string? description = null)
        : base(name, [])
    {
        Description = description ?? "";
        Recursive = true;
    }

    public GlobalOption(string name, string[] aliases, string? description = null)
        : base(name, aliases)
    {
        Description = description ?? "";
        Recursive = true;
    }
}

public abstract partial class OptionPack
{
    /// <inheritdoc cref="AddGeneratedOptions"/>
    protected virtual void AddGeneratedOptions(Command cmd) { }

    /// <summary>The heading shown for this pack's options section in help output. Empty = no group.</summary>
    public virtual string HelpTitle => "";

    /// <summary>Optional description shown beneath the section heading.</summary>
    public virtual string HelpSectionDescription => "";

    private ParseResult _result = null!;

    internal void SetParseResult(ParseResult r) => _result = r;

    protected T GetValue<T>(Option<T> option) => _result.GetValue(option)!;

    /// <summary>Called by the template to wire nested OptionPack fields. Overridden by the generator.</summary>
    protected virtual void AddChildPacksTo(Command cmd) { }

    /// <summary>Fallback for options added manually (non-generator path).</summary>
    protected virtual void AddManualOptions(Command cmd) { }

    internal void AddOptionsTo(Command cmd)
    {
        int countBeforeChildren = cmd.Options.Count;
        AddChildPacksTo(cmd);
        int countAfterChildren = cmd.Options.Count;

        AddGeneratedOptions(cmd);
        AddManualOptions(cmd);

        // Tag own options (after child options) with this pack's section info.
        if (!string.IsNullOrEmpty(HelpTitle))
        {
            for (int i = countAfterChildren; i < cmd.Options.Count; i++)
                HelpGroupRegistry.Tag(cmd.Options[i], HelpTitle, HelpSectionDescription);
        }
    }
}
