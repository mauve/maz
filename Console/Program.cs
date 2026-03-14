using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Help;
using Azure.Core;
using Console.Cli;
using Console.Rendering;

// Register per-type field visibility for the text renderer.
// Only the token value is shown by default; use --show-all for full metadata.
TextFieldRegistry.RegisterVisibleFields<AccessToken>("Token");

var rootDef = new RootCommandDef();
var rootCmd = rootDef.Build();

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
        Description = "Show all options including advanced ones, and detailed command descriptions.",
        Hidden = !isRoot,
    };
    if (helpMore.Action is HelpAction helpMoreAction)
        helpMoreAction.Builder.CustomizeLayout(GroupedHelpLayout.CreateWithAdvanced);
    cmd.Add(helpMore);

    var helpCommands = new Option<string?>("--help-commands", [])
    {
        Description = "Show the full command tree. Optionally filter by name, alias, or description.",
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

((RootCommand)rootCmd).Add(new SuggestDirective());
var config = new CommandLineConfiguration(rootCmd);
return rootCmd.Parse(args, config).Invoke();

static IEnumerable<Command> AllCommands(Command root)
{
    yield return root;
    foreach (var sub in root.Subcommands)
    foreach (var cmd in AllCommands(sub))
        yield return cmd;
}
