// ParseResult.GetValue is annotated [MaybeNull] but callers handle nullability via option defaults
#pragma warning disable CS8603
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Reflection;
using Azure.Identity;

namespace Console.Cli;

public abstract partial class CommandDef
{
    /// <inheritdoc cref="AddGeneratedOptions"/>
    protected virtual void AddGeneratedOptions(Command cmd) { }

    /// <inheritdoc cref="AddGeneratedChildren"/>
    protected virtual void AddGeneratedChildren(Command cmd) { }

    /// <summary>True when the generator has wired up child OptionPacks and CommandDefs.</summary>
    protected virtual bool HasGeneratedChildren => false;

    /// <summary>
    /// True when this command operates against a data-plane endpoint
    /// (e.g. Key Vault data API) rather than ARM.
    /// Overridden to <c>true</c> by generated commands for data-plane services.
    /// </summary>
    protected virtual bool IsDataPlane => false;

    /// <summary>
    /// True when this command performs a destructive operation (HTTP DELETE, PUT, or POST).
    /// Overridden to <c>true</c> by generated commands for such operations and manually-written
    /// destructive commands. Used to attach the global <c>--require-confirmation</c> guard.
    /// </summary>
    protected virtual bool IsDestructive => false;

    /// <summary>
    /// True when this command overrides <see cref="ExecuteAsync"/>.
    /// Replaces a runtime reflection check. Emitted <c>true</c> by the source generator for
    /// leaf commands and by the spec generator for operation commands.
    /// </summary>
    protected virtual bool HasExecuteHandler => false;

    /// <summary>
    /// True for <see cref="RootCommandDef"/>. Used to customize root-only help options.
    /// </summary>
    protected virtual bool IsRootCommand => false;

    public abstract string Name { get; }
    public virtual string[] Aliases => [];
    public virtual string Description => "";
    public virtual string? DetailedDescription => Remarks;

    protected virtual string? Remarks => null;

    protected ParseResult ParseResult { get; private set; } = null!;
    protected bool HasParseResult => ParseResult is not null;

    protected T GetValue<T>(Option<T> option) => ParseResult.GetValue(option)!;

    protected T GetValue<T>(Argument<T> argument) => ParseResult.GetValue(argument);

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
    /// Adds this command's generated subcommands directly into <paramref name="parentCmd"/>,
    /// bypassing the wrapping Command node. Used when this command's <see cref="Name"/> is
    /// identical to the parent command's name, which would cause a duplicate-key error in
    /// System.CommandLine's token map.
    /// </summary>
    internal void AddSubcommandsTo(Command parentCmd) => AddGeneratedChildren(parentCmd);

    internal virtual Command Build()
    {
        var cmd = CreateCommand();
        ConfigureCommand(cmd);
        if (DetailedDescription is { } r)
            RemarksRegistry.Register(cmd, r);
        if (IsDataPlane)
            DataPlaneRegistry.Register(cmd);
        if (IsDestructive)
        {
            DestructiveCommandRegistry.Register(cmd);
            var original = cmd.Action;
            if (original is not null)
                cmd.SetAction(
                    async (parseResult, ct) =>
                    {
                        var requireConfirm = parseResult.GetValue(
                            Shared.GlobalBehaviorOptionPack.RequireConfirmationOption
                        );
                        if (requireConfirm)
                        {
                            var interactive = Shared.InteractiveOptionPack.IsEffectivelyInteractive(
                                parseResult.GetValue(Shared.InteractiveOptionPack.InteractiveOption)
                            );
                            PromptForConfirmation(interactive, cmd.Name);
                        }

                        return original switch
                        {
                            AsynchronousCommandLineAction asyncAction =>
                                await asyncAction.InvokeAsync(parseResult, ct),
                            SynchronousCommandLineAction syncAction => syncAction.Invoke(
                                parseResult
                            ),
                            _ => 0,
                        };
                    }
                );
        }
        return cmd;
    }

    protected virtual Command CreateCommand()
    {
        var cmd = new Command(Name, Description);
        foreach (var alias in Aliases)
            cmd.Aliases.Add(alias);
        return cmd;
    }

    private void ConfigureCommand(Command cmd)
    {
        AddGeneratedOptions(cmd);
        AddGeneratedChildren(cmd);

        // Only scan fields for hand-written commands that declare Option<T>/Argument<T> manually.
        // Generated commands (HasGeneratedChildren = true) have no such fields.
        if (!HasGeneratedChildren)
        {
            foreach (var field in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var value = field.GetValue(this);
                if (value is null)
                    continue;

                if (value is OptionPack pack)
                    pack.AddOptionsTo(cmd);
                else if (value is CommandDef subDef)
                    cmd.Add(subDef.Build());
                else if (value is Option opt)
                    cmd.Add(opt);
                else if (value is Argument arg)
                    cmd.Add(arg);
            }
        }

        bool hasHandler = HasExecuteHandler;

        var self = this;
        if (hasHandler)
        {
            cmd.SetAction(
                async (result, ct) =>
                {
                    self.ParseResult = result;
                    InjectParseResult(self, result);
                    try
                    {
                        return await self.ExecuteAsync(ct);
                    }
                    catch (InvocationException ex)
                    {
                        System.Console.Error.WriteLine(ex.Message);
                        return ex.ExitCode;
                    }
                    catch (AuthenticationFailedException ex)
                    {
                        var authPack = self.GetAuthOptionPack();
                        var configured = authPack?.AllowedCredentialTypes;
                        System.Console.Error.WriteLine(
                            AuthenticationErrorFormatter.Format(ex, configured)
                        );
                        if (result.GetValue(Shared.DiagnosticOptionPack.DetailedErrorsOption))
                            System.Console.Error.WriteLine(ex.ToString());
                        return 1;
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        System.Console.Error.WriteLine(HttpRequestErrorFormatter.Format(ex));
                        if (result.GetValue(Shared.DiagnosticOptionPack.DetailedErrorsOption))
                            System.Console.Error.WriteLine(ex.ToString());
                        return 1;
                    }
                }
            );
        }
        else
        {
            cmd.Action = new HelpAction();
        }

        // Customize help layout for all commands.
        if (cmd.Options.OfType<HelpOption>().FirstOrDefault() is { Action: HelpAction optHelp })
            optHelp.Builder.CustomizeLayout(GroupedHelpLayout.Create);
        if (cmd.Action is HelpAction cmdHelp)
            cmdHelp.Builder.CustomizeLayout(GroupedHelpLayout.Create);

        var isRoot = IsRootCommand;

        var helpMore = new HelpOption("--help-more", [])
        {
            Description =
                "Show all options including advanced ones, and detailed command descriptions.",
            Hidden = !isRoot,
        };
        if (helpMore.Action is HelpAction helpMoreAction)
            helpMoreAction.Builder.CustomizeLayout(GroupedHelpLayout.CreateWithAdvanced);
        cmd.Add(helpMore);

        var helpCommands = new Option<string?>("--help-commands", [])
        {
            Description =
                "Show the full command tree. Optionally filter by name, alias, or description.",
            Hidden = !isRoot,
            Arity = ArgumentArity.ZeroOrOne,
        };
        helpCommands.Action = new CommandTreeAction(cmd, helpCommands);
        cmd.Add(helpCommands);

        var helpCommandsFlat = new Option<string?>("--help-commands-flat", [])
        {
            Description = "Show all commands as a flat list. Optionally filter by name or alias.",
            Hidden = !isRoot,
            Arity = ArgumentArity.ZeroOrOne,
        };
        helpCommandsFlat.Action = new CommandFlatAction(cmd, helpCommandsFlat);
        cmd.Add(helpCommandsFlat);

        const string helpGroup = "Help";
        if (isRoot)
        {
            // Group --help-more and --help-commands under a visible "Help" section on the root command.
            // --help is intentionally left untagged so it stays in the ungrouped "Options:" section
            // on every command (it reaches subcommands via HelpOption's built-in Recursive=true).
            if (cmd.Options.OfType<HelpOption>().FirstOrDefault() is { } helpOpt)
                helpOpt.Description = "Show usage information for the current command.";
            HelpGroupRegistry.Tag(helpMore, helpGroup, "");
            HelpGroupRegistry.Tag(helpCommands, helpGroup, "");
            HelpGroupRegistry.Tag(helpCommandsFlat, helpGroup, "");
        }
        else
        {
            // HelpOption is recursive, so the root's --help-more bleeds into subcommand help via
            // AllOptions. Suppress the "Help" group on every non-root command so it stays clean.
            HiddenGroupRegistry.HideGroup(cmd, helpGroup);
        }
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

    private static void InjectParseResult(object obj, ParseResult result) =>
        InjectParseResult(obj, result, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static void InjectParseResult(object obj, ParseResult result, HashSet<object> visited)
    {
        if (!visited.Add(obj))
            return;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
            {
                if (field.GetValue(obj) is OptionPack pack)
                {
                    pack.SetParseResult(result);
                    InjectParseResult(pack, result, visited);
                }
            }
        }
    }
}
