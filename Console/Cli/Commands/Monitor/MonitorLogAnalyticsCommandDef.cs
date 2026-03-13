using Console.Cli.Shared;

namespace Console.Cli.Commands.Monitor;

public class MonitorLogAnalyticsCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "log-analytics";
    public override string[] Aliases => ["logs"];
    public override string Description => "Query LogAnalytics workspaces for logs and metrics.";

    public readonly MonitorLogAnalyticsQueryCommandDef Query = new(auth);
}
