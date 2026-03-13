using System.CommandLine;
using System.CommandLine.Help;
using Console.Cli;

var rootDef = new RootCommandDef();
var rootCmd = rootDef.Build();

// Find the shared HelpBuilder and apply grouped layout.
if (rootCmd.Options.OfType<HelpOption>().FirstOrDefault() is { Action: HelpAction helpAction })
    helpAction.Builder.CustomizeLayout(GroupedHelpLayout.Create);

var config = new CommandLineConfiguration(rootCmd);
return rootCmd.Parse(args, config).Invoke();
