using System.Diagnostics;
using Azure.Core;
using Console;
using Console.Cli;
using Console.Cli.Parsing;
using Console.Cli.Shared;
using Console.Config;
using Console.Rendering;

// Register per-type field visibility for the text renderer.
TextFieldRegistry.RegisterVisibleFields<AccessToken>("Token");

// Load user config (before command tree is built).
MazConfig.Initialize();

if (args is [var first, ..] && first.StartsWith("[suggest:") && first.EndsWith(']'))
{
    var pos = int.Parse(first[9..^1]);
    var line = args.Length >= 2 ? args[1] : "";
    await CliCompletionHandler.HandleAsync(line, pos);
    return 0;
}

if (args is ["--version"])
{
    System.Console.WriteLine(RootCommandDef.GetVersion());
    return 0;
}

// Detect if we need the full tree (--help-commands shows all commands).
bool needsFullTree = args.Any(a =>
    a is "--help-commands" or "--help-commands-flat"
    || a.StartsWith("--help-commands=", StringComparison.Ordinal)
    || a.StartsWith("--help-commands-flat=", StringComparison.Ordinal)
);

// System.CommandLine won't consume a known subcommand name as the ZeroOrOne value for
// --help-commands / --help-commands-flat, routing it as a subcommand instead and losing
// the filter. Rewrite "--help-commands foo" -> "--help-commands=foo" to force consumption.
args = RewriteHelpCommandsFilter(args);

// First non-option, non-directive arg that matches a known service is the target service.
var candidate = args.FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('[') && a.Length > 0);
string? targetService =
    needsFullTree ? null
    : candidate is not null && RootCommandDef.KnownServices.Contains(candidate) ? candidate
    : null;

var rootDef = new RootCommandDef(targetService);

// Apply [global] option defaults (for non-string types like enums) and [cmd.X] per-command defaults.
ApplyCommandDefaults(rootDef, MazConfig.Current);

var result = CliParser.Parse(args, rootDef);

// Handle directives
foreach (var directive in result.Directives)
{
    switch (directive.Name)
    {
        case "debug":
            System.Console.Error.WriteLine(
                $"Waiting for debugger to attach to PID {Environment.ProcessId}..."
            );
            while (!System.Diagnostics.Debugger.IsAttached)
                Thread.Sleep(100);
            System.Console.Error.WriteLine("Debugger attached.");
            if (
                directive.Value is { } delayStr
                && int.TryParse(delayStr, out var delaySec)
                && delaySec > 0
            )
            {
                System.Console.Error.WriteLine($"Waiting {delaySec}s before continuing...");
                Thread.Sleep(delaySec * 1000);
            }
            break;
        case "suggest":
            // Already handled above (pre-parse path for performance).
            // If we get here, it means the directive was mixed in with regular args.
            if (directive.Value is { } posStr && int.TryParse(posStr, out var suggestPos))
            {
                var line = args.Length >= 2 ? args[1] : "";
                await CliCompletionHandler.HandleAsync(line, suggestPos);
                return 0;
            }
            break;
    }
}

// Handle help options before error checking
if (result.Command is not null)
{
    var cmd = result.Command;

    if (cmd._helpOption.WasProvided)
        return cmd.ShowHelp(showAdvanced: false);

    if (cmd._helpMoreOption.WasProvided)
        return cmd.ShowHelp(showAdvanced: true);

    if (cmd._helpCommandsOption.WasProvided)
    {
        var filter = cmd._helpCommandsOption.Value;
        if (
            !System.Console.IsInputRedirected
            && !System.Console.IsOutputRedirected
            && Ansi.IsEnabled
        )
        {
            InteractiveCommandTree.Run(rootDef, filter);
            return 0;
        }
        CommandTreePrinter.Print(System.Console.Out, rootDef, filter);
        return 0;
    }

    if (cmd._helpCommandsFlatOption.WasProvided)
    {
        var filter = cmd._helpCommandsFlatOption.Value;
        CommandTreePrinter.PrintFlat(System.Console.Out, rootDef, filter);
        return 0;
    }
}

if (result.Errors.Count > 0)
{
    var interactive = InteractiveOptionPack.IsEffectivelyInteractiveFromTree(
        result.Command ?? rootDef
    );

    var suggestionResult = CommandSuggester.TrySuggest(
        result,
        args,
        interactive,
        System.Console.Error,
        System.Console.ReadLine,
        rootDef
    );

    if (suggestionResult >= 0)
        return suggestionResult;

    // No suggestions — fall back to printing raw errors
    foreach (var error in result.Errors)
        System.Console.Error.WriteLine(Ansi.Red(error));
    return 1;
}

// If the matched command has no execute handler, show help
if (result.Command is { } leafCmd && !leafCmd.HasExecuteHandler)
    return leafCmd.ShowHelp();

return await result.Command!.InvokeAsync(CancellationToken.None);

static string[] RewriteHelpCommandsFilter(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--help-commands" or "--help-commands-flat")
        {
            var next = args[i + 1];
            if (next.Length > 0 && !next.StartsWith('-'))
            {
                var rewritten = args.ToList();
                rewritten[i] = $"{args[i]}={next}";
                rewritten.RemoveAt(i + 1);
                return rewritten.ToArray();
            }
            break;
        }
    }
    return args;
}

static void ApplyCommandDefaults(CommandDef root, MazConfig mazConfig)
{
    if (mazConfig.GlobalDefaults.Count > 0)
    {
        foreach (var cmd in AllCommands(root))
        {
            foreach (var (key, value) in mazConfig.GlobalDefaults)
            {
                foreach (var opt in cmd.EnumerateAllOptions())
                {
                    var optKey = opt.Name.TrimStart('-');
                    if (optKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        TrySetDefault(opt, value);
                        break;
                    }
                }
            }
        }
    }

    if (mazConfig.CommandDefaults.Count > 0)
    {
        ApplyDefaultsRecursive(root, "", mazConfig);
    }
}

static void ApplyDefaultsRecursive(CommandDef cmd, string parentPath, MazConfig mazConfig)
{
    var path = parentPath.Length > 0 ? $"{parentPath} {cmd.Name}" : cmd.Name;

    if (mazConfig.CommandDefaults.TryGetValue(path, out var defaults))
    {
        foreach (var (key, value) in defaults)
        {
            foreach (var opt in cmd.EnumerateAllOptions())
            {
                var optKey = opt.Name.TrimStart('-');
                if (optKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    TrySetDefault(opt, value);
                    break;
                }
            }
        }
    }

    foreach (var sub in cmd.EnumerateChildren())
        ApplyDefaultsRecursive(sub, path, mazConfig);
}

static IEnumerable<CommandDef> AllCommands(CommandDef root)
{
    yield return root;
    foreach (var sub in root.EnumerateChildren())
    foreach (var cmd in AllCommands(sub))
        yield return cmd;
}

static void TrySetDefault(Console.Cli.Parsing.CliOption opt, string value)
{
    // For now, just try to parse the value. If the option has a custom parser, it will use it.
    opt.TryParse(value);
    // Reset WasProvided since this is a default, not user input
    opt.WasProvided = false;
}
