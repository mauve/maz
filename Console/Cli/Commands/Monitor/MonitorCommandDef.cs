using Console.Cli.Shared;

namespace Console.Cli.Commands.Monitor;

/// <summary>Manage Azure Monitor operations.</summary>
/// <remarks>
/// This group contains commands for querying monitoring data sources.
/// Use it to drill into Log Analytics workspaces and Azure resource logs.
/// </remarks>
public partial class MonitorCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "monitor";
    public override string[] Aliases => ["mon"];

    public readonly MonitorLogAnalyticsCommandDef LogAnalytics = new(auth);
}
