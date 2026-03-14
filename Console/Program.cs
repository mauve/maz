using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Linq.Expressions;
using System.Reflection;
using Azure.Core;
using Console;
using Console.Cli;
using Console.Cli.Shared;
using Console.Config;
using Console.Rendering;

// Register per-type field visibility for the text renderer.
// Only the token value is shown by default; use --show-all for full metadata.
TextFieldRegistry.RegisterVisibleFields<AccessToken>("Token");

// Load user config and inject [global] defaults as env vars (before command tree is built).
MazConfig.Initialize();
MazConfig.Current.InjectEnvironmentDefaults();

if (args is [var first, ..] && first.StartsWith("[suggest:") && first.EndsWith(']'))
{
    var pos = int.Parse(first[9..^1]);
    var line = args.Length >= 2 ? args[1] : "";
    await CliCompletionHandler.HandleAsync(line, pos, new RootCommandDef());
    return 0;
}

var rootDef = new RootCommandDef();
var rootCmd = rootDef.Build();

// Apply [global] option defaults (for non-string types like enums) and [cmd.X] per-command defaults.
ApplyCommandDefaults(rootCmd, MazConfig.Current);

// Wrap destructive commands with the global --require-confirmation guard.
ApplyRequireConfirmationGuard(rootCmd);

// Apply grouped layout to every HelpAction in the tree and add --help-more to each command.
foreach (var cmd in AllCommands(rootCmd))
{
    if (cmd.Options.OfType<HelpOption>().FirstOrDefault() is { Action: HelpAction optHelp })
        optHelp.Builder.CustomizeLayout(GroupedHelpLayout.Create);
    if (cmd.Action is HelpAction cmdHelp)
        cmdHelp.Builder.CustomizeLayout(GroupedHelpLayout.Create);

    var isRoot = cmd == rootCmd;

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
    }
    else
    {
        // HelpOption is recursive, so the root's --help-more bleeds into subcommand help via
        // AllOptions. Suppress the "Help" group on every non-root command so it stays clean.
        HiddenGroupRegistry.HideGroup(cmd, helpGroup);
    }
}

var config = new CommandLineConfiguration(rootCmd);
var result = rootCmd.Parse(args, config);

if (result.Errors.Count > 0)
{
    var interactive = InteractiveOptionPack.IsEffectivelyInteractive(
        result.GetValue(InteractiveOptionPack.InteractiveOption)
    );

    var suggestionResult = CommandSuggester.TrySuggest(
        result,
        args,
        interactive,
        System.Console.Error,
        System.Console.ReadLine
    );

    if (suggestionResult >= 0)
        return suggestionResult;

    // No suggestions — fall back to printing raw errors
    foreach (var error in result.Errors)
        System.Console.Error.WriteLine(Ansi.Red(error.Message));
    return 1;
}

return result.Invoke();

static IEnumerable<Command> AllCommands(Command root)
{
    yield return root;
    foreach (var sub in root.Subcommands)
    foreach (var cmd in AllCommands(sub))
        yield return cmd;
}

static void ApplyCommandDefaults(Command root, MazConfig mazConfig)
{
    // Apply [global] defaults to all commands for option types not handled by env-var injection
    // (e.g. enum options like --format which the source generator can't fall back via ??)
    if (mazConfig.GlobalDefaults.Count > 0)
    {
        foreach (var cmd in AllCommands(root))
        {
            foreach (var (key, value) in mazConfig.GlobalDefaults)
            {
                var opt = cmd.Options.FirstOrDefault(o =>
                    o.Name.TrimStart('-').Equals(key, StringComparison.OrdinalIgnoreCase)
                );
                if (opt is not null)
                    TrySetDefault(opt, value);
            }
        }
    }

    // Apply [cmd.X] per-command overrides (take precedence over global defaults)
    if (mazConfig.CommandDefaults.Count > 0)
    {
        foreach (var sub in root.Subcommands)
            ApplyDefaultsRecursive(sub, "", mazConfig);
    }
}

static void ApplyDefaultsRecursive(Command cmd, string parentPath, MazConfig mazConfig)
{
    var path = parentPath.Length > 0 ? $"{parentPath} {cmd.Name}" : cmd.Name;

    if (mazConfig.CommandDefaults.TryGetValue(path, out var defaults))
    {
        foreach (var (key, value) in defaults)
        {
            var opt = cmd.Options.FirstOrDefault(o =>
                o.Name.TrimStart('-').Equals(key, StringComparison.OrdinalIgnoreCase)
            );
            if (opt is not null)
                TrySetDefault(opt, value);
        }
    }

    foreach (var sub in cmd.Subcommands)
        ApplyDefaultsRecursive(sub, path, mazConfig);
}

static void TrySetDefault(Option opt, string value)
{
    var genericArg = opt.GetType().GetGenericArguments().FirstOrDefault();
    if (genericArg is null)
        return;

    // Parse the string value to the target option type
    object? parsed;

    if (genericArg == typeof(string))
    {
        parsed = value;
    }
    else if (genericArg == typeof(bool))
    {
        if (!bool.TryParse(value, out var bv))
            return;
        parsed = bv;
    }
    else if (genericArg.IsEnum)
    {
        if (!Enum.TryParse(genericArg, value, ignoreCase: true, out parsed))
            return;
    }
    else if (
        Nullable.GetUnderlyingType(genericArg) is { } underlying
        && underlying.IsEnum
    )
    {
        if (!Enum.TryParse(underlying, value, ignoreCase: true, out var ev))
            return;
        parsed = ev;
    }
    else
    {
        return;
    }

    // Set DefaultValueFactory via reflection (Option<T>.DefaultValueFactory = Func<ArgumentResult, T>)
    var dfProp = opt.GetType().GetProperty("DefaultValueFactory");
    if (dfProp is null)
        return;

    // Build a Func<ArgumentResult, T> that returns the parsed value, using expression trees
    // so we don't need to reference ArgumentResult directly.
    var argResultType = dfProp.PropertyType.GetGenericArguments()[0];
    var paramExpr = Expression.Parameter(argResultType, "_");
    var valueExpr = Expression.Constant(parsed, genericArg);
    var funcType = typeof(Func<,>).MakeGenericType(argResultType, genericArg);
    var factory = Expression.Lambda(funcType, valueExpr, paramExpr).Compile();
    dfProp.SetValue(opt, factory);
}

static void ApplyRequireConfirmationGuard(Command rootCmd)
{
    foreach (var cmd in AllCommands(rootCmd))
    {
        if (!DestructiveCommandRegistry.IsDestructive(cmd))
            continue;

        var original = cmd.Action;
        if (original is null)
            continue;

        cmd.SetAction(async (parseResult, ct) =>
        {
            var requireConfirm = parseResult.GetValue(
                GlobalBehaviorOptionPack.RequireConfirmationOption
            );
            if (requireConfirm)
            {
                var interactive = InteractiveOptionPack.IsEffectivelyInteractive(
                    parseResult.GetValue(InteractiveOptionPack.InteractiveOption)
                );
                PromptForConfirmation(interactive, cmd.Name);
            }

            return original switch
            {
                AsynchronousCommandLineAction asyncAction =>
                    await asyncAction.InvokeAsync(parseResult, ct),
                SynchronousCommandLineAction syncAction => syncAction.Invoke(parseResult),
                _ => 0,
            };
        });
    }
}

static void PromptForConfirmation(bool interactive, string commandName)
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
