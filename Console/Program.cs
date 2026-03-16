using System.CommandLine;
using System.CommandLine.Help;
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
    await CliCompletionHandler.HandleAsync(line, pos);
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

// First non-option arg that matches a known service is the target service.
var candidate = args.FirstOrDefault(a => !a.StartsWith('-') && a.Length > 0);
string? targetService =
    needsFullTree ? null
    : candidate is not null && RootCommandDef.KnownServices.Contains(candidate) ? candidate
    : null;

var rootDef = new RootCommandDef(targetService);
var rootCmd = rootDef.Build();

// Apply [global] option defaults (for non-string types like enums) and [cmd.X] per-command defaults.
ApplyCommandDefaults(rootCmd, MazConfig.Current);

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
    else if (Nullable.GetUnderlyingType(genericArg) is { } underlying && underlying.IsEnum)
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
