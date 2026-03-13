using Console.Cli.Shared;

namespace Console.Cli.Commands.Monitor;

public class MonitorCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "monitor";
    public override string[] Aliases => ["mon"];
    public override string Description => "Command group for Azure Monitor.";

    public readonly MonitorLogAnalyticsCommandDef LogAnalytics = new(auth);
}
