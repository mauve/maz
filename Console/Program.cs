using System.CommandLine;
using Console.Cli;

var rootDef = new RootCommandDef();
var rootCmd = rootDef.Build();
var config = new CommandLineConfiguration(rootCmd);
return rootCmd.Parse(args, config).Invoke();
