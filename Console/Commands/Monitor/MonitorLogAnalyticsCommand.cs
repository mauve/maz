using DotMake.CommandLine;

namespace Console.Commands.Monitor;

[CliCommand(
    Description = "Query LogAnalytics workspaces for logs and metrics.",
    Parent = typeof(MonitorCommand),
    Name = "log-analytics",
    Aliases = ["logs"]
)]
public class MonitorLogAnalyticsCommand
{
    public required MonitorCommand Parent { get; set; }
}
