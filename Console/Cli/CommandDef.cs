// ParseResult.GetValue is annotated [MaybeNull] but callers handle nullability via option defaults
#pragma warning disable CS8603
using System.Reflection;
using Azure.Identity;
using Console.Cli.Parsing;

namespace Console.Cli;

public abstract partial class CommandDef
{
    // ── New enumeration methods (used by CliParser) ─────────────────────

    /// <summary>Yields this command's own options (not including child packs).</summary>
    internal virtual IEnumerable<CliOption> EnumerateOptions()
    {
        yield break;
    }

    /// <summary>Yields child CommandDefs.</summary>
    internal virtual IEnumerable<CommandDef> EnumerateChildren()
    {
        yield break;
    }

    /// <summary>Yields positional arguments declared on this command.</summary>
    internal virtual IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield break;
    }

    /// <summary>
    /// Yields all options for this command, including options from child OptionPacks
    /// and built-in help options. This is what the parser and help layout use.
    /// </summary>
    internal virtual IEnumerable<CliOption> EnumerateAllOptions()
    {
        // Built-in help options
        yield return _helpOption;
        yield return _helpMoreOption;
        yield return _helpCommandsOption;
        yield return _helpCommandsFlatOption;

        // Own options
        foreach (var opt in EnumerateOptions())
            yield return opt;

        // Options from OptionPack children (via generated EnumerateOptionPacks)
        foreach (var pack in EnumerateOptionPacks())
        {
            foreach (var opt in pack.EnumerateAllOptions())
                yield return opt;
        }
    }

    /// <summary>Yields child OptionPacks. Overridden by generator.</summary>
    internal virtual IEnumerable<OptionPack> EnumerateOptionPacks()
    {
        yield break;
    }

    /// <summary>
    /// Yields all options recursively available to this command, including
    /// recursive options from all ancestors in the command path.
    /// </summary>
    internal IEnumerable<CliOption> EnumerateAllOptionsWithRecursive(List<CommandDef> commandPath)
    {
        // Own options (including from packs)
        foreach (var opt in EnumerateAllOptions())
            yield return opt;

        // Recursive options from ancestors
        var seen = new HashSet<string>(EnumerateAllOptions().Select(o => o.Name));
        for (int i = commandPath.Count - 2; i >= 0; i--)
        {
            foreach (var opt in commandPath[i].EnumerateAllOptions())
            {
                if (opt.Recursive && seen.Add(opt.Name))
                    yield return opt;
            }
        }
    }

    // ── Help options (built into every command) ────────────────────────

    internal readonly CliOption<bool> _helpOption = new()
    {
        Name = "--help",
        Aliases = ["-h", "-?"],
        Description = "Show usage information for the current command.",
        Hidden = false,
        Recursive = true,
    };

    internal readonly CliOption<bool> _helpMoreOption = new()
    {
        Name = "--help-more",
        Description = "Show all options including advanced ones, and detailed command descriptions.",
    };

    internal readonly CliOption<string?> _helpCommandsOption = new()
    {
        Name = "--help-commands",
        Description = "Show the full command tree. Optionally filter by name, alias, or description.",
        ValueIsOptional = true,
    };

    internal readonly CliOption<string?> _helpCommandsFlatOption = new()
    {
        Name = "--help-commands-flat",
        Description = "Show all commands as a flat list. Optionally filter by name or alias.",
        ValueIsOptional = true,
    };

    // ── Properties ─────────────────────────────────────────────────────

    /// <summary>True when the generator has wired up child OptionPacks and CommandDefs.</summary>
    protected virtual bool HasGeneratedChildren => false;

    /// <summary>
    /// True when this command operates against a data-plane endpoint.
    /// </summary>
    protected internal virtual bool IsDataPlane => false;

    /// <summary>
    /// True when this command performs a destructive operation.
    /// </summary>
    protected internal virtual bool IsDestructive => false;

    /// <summary>
    /// True when this command overrides <see cref="ExecuteAsync"/>.
    /// </summary>
    protected internal virtual bool HasExecuteHandler => false;

    /// <summary>
    /// True when this command is a manually crafted (non-generated) command.
    /// </summary>
    protected internal virtual bool IsManualCommand => false;

    /// <summary>
    /// True for <see cref="RootCommandDef"/>.
    /// </summary>
    protected virtual bool IsRootCommand => false;

    /// <summary>Set of group titles to hide in help for this command.</summary>
    internal HashSet<string>? HiddenHelpGroups { get; set; }

    public abstract string Name { get; }
    public virtual string[] Aliases => [];
    public virtual string Description => "";
    public virtual string? DetailedDescription => Remarks;

    protected virtual string? Remarks => null;

    // ── Value access (new path: reads directly from CliOption) ─────────
    // These must remain instance methods because generated code calls this.GetValue(...).
#pragma warning disable CA1822
    protected T GetValue<T>(CliOption<T> option) => option.Value!;

    protected T GetValue<T>(CliArgument<T> argument) => argument.Value!;
#pragma warning restore CA1822

    protected virtual Task<int> ExecuteAsync(CancellationToken cancellationToken) =>
        Task.FromResult(0);

    private Shared.AuthOptionPack? GetAuthOptionPack()
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
            {
                if (field.GetValue(this) is Shared.AuthOptionPack pack)
                    return pack;
            }
        }
        return null;
    }

    /// <summary>
    /// Inlines this command's generated subcommands.
    /// Used when this command's Name collides with the parent's name.
    /// </summary>
    internal IEnumerable<CommandDef> InlineChildren() => EnumerateChildren();

    // ── Execution ──────────────────────────────────────────────────────

    /// <summary>
    /// Execute this command. Called by Program.cs after parsing.
    /// Handles destructive confirmation, exception formatting, etc.
    /// </summary>
    internal async Task<int> InvokeAsync(CancellationToken ct)
    {
        if (IsDestructive)
        {
            var requireConfirm = false;
            foreach (var opt in EnumerateAllOptions())
            {
                if (opt.Name == "--require-confirmation" && opt is CliOption<bool> boolOpt)
                {
                    requireConfirm = boolOpt.Value;
                    break;
                }
            }

            if (requireConfirm)
            {
                var interactive = Shared.InteractiveOptionPack.IsEffectivelyInteractiveFromTree(this);
                PromptForConfirmation(interactive, Name);
            }
        }

        try
        {
            return await ExecuteAsync(ct);
        }
        catch (InvocationException ex)
        {
            System.Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (AuthenticationFailedException ex)
        {
            var authPack = GetAuthOptionPack();
            var configured = authPack?.AllowedCredentialTypes;
            System.Console.Error.WriteLine(
                AuthenticationErrorFormatter.Format(ex, configured)
            );
            if (IsDetailedErrorsEnabled())
                System.Console.Error.WriteLine(ex.ToString());
            return 1;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            System.Console.Error.WriteLine(HttpRequestErrorFormatter.Format(ex));
            if (IsDetailedErrorsEnabled())
                System.Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private bool IsDetailedErrorsEnabled()
    {
        foreach (var opt in EnumerateAllOptions())
        {
            if (opt.Name == "--detailed-errors" && opt is CliOption<bool> boolOpt)
                return boolOpt.Value;
        }
        return false;
    }

    // ── Help ───────────────────────────────────────────────────────────

    internal int ShowHelp(bool showAdvanced = false)
    {
        GroupedHelpLayout.Render(System.Console.Out, this, showAdvanced);
        return 0;
    }

    private static void PromptForConfirmation(bool interactive, string commandName)
    {
        if (!interactive || System.Console.IsInputRedirected)
            throw new InvocationException(
                $"Operation '{commandName}' is destructive and --require-confirmation is enabled. "
                    + "Run interactively and confirm, or disable --require-confirmation."
            );

        System.Console.Write($"Operation '{commandName}' is destructive. Proceed? (y/N): ");
        var response = System.Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response is not ("y" or "yes"))
            throw new InvocationException("Operation cancelled by user.");
    }
}
