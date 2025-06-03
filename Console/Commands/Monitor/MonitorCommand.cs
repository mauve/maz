using DotMake.CommandLine;

namespace Console.Commands.Monitor;

[CliCommand(
    Description = "Command group for Azure Monitor.",
    Parent = typeof(RootCommand),
    Aliases = ["mon"]
)]
public class MonitorCommand
{
    public required RootCommand Parent { get; set; }
}
