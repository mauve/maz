using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Help;
using Console.Cli;
using Console.Rendering;

var rootDef = new RootCommandDef();
var rootCmd = rootDef.Build();

// Apply grouped layout to every HelpAction in the tree and add --help-more to each command.
foreach (var cmd in AllCommands(rootCmd))
{
    if (cmd.Options.OfType<HelpOption>().FirstOrDefault() is { Action: HelpAction optHelp })
        optHelp.Builder.CustomizeLayout(GroupedHelpLayout.Create);
    if (cmd.Action is HelpAction cmdHelp)
        cmdHelp.Builder.CustomizeLayout(GroupedHelpLayout.Create);

    var helpMore = new HelpOption("--help-more", [])
    {
        Description = "Show help including advanced options and detailed descriptions.",
        Hidden = true,
    };
    if (helpMore.Action is HelpAction helpMoreAction)
        helpMoreAction.Builder.CustomizeLayout(GroupedHelpLayout.CreateWithAdvanced);
    cmd.Add(helpMore);
}

((RootCommand)rootCmd).Add(new SuggestDirective());
var config = new CommandLineConfiguration(rootCmd);
var result = rootCmd.Parse(args, config);

if (result.Errors.Count > 0 && result.Action is not HelpAction)
{
    var cmd = result.CommandResult.Command;
    bool showAdvanced = Array.Exists(args, a => a == "--help-more");

    var helpOpt = showAdvanced
        ? cmd.Options.OfType<HelpOption>().FirstOrDefault(o => o.Aliases.Contains("--help-more"))
        : cmd.Options.OfType<HelpOption>().FirstOrDefault(o => !o.Aliases.Contains("--help-more"));

    if (helpOpt?.Action is HelpAction helpAction)
        helpAction.Invoke(result);

    System.Console.Error.WriteLine();
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
