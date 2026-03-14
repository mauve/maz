using System.CommandLine;
using System.CommandLine.Help;
using Console.Cli;

if (args is [var first, ..] && first.StartsWith("[suggest:") && first.EndsWith(']'))
{
    var pos = int.Parse(first[9..^1]);
    var line = args.Length >= 2 ? args[1] : "";
    await CliCompletionHandler.HandleAsync(line, pos, new RootCommandDef());
    return 0;
}

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

var config = new CommandLineConfiguration(rootCmd);
return rootCmd.Parse(args, config).Invoke();

static IEnumerable<Command> AllCommands(Command root)
{
    yield return root;
    foreach (var sub in root.Subcommands)
    foreach (var cmd in AllCommands(sub))
        yield return cmd;
}
