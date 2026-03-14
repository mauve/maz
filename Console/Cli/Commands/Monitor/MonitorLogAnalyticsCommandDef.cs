using Console.Cli.Shared;

namespace Console.Cli.Commands.Monitor;

/// <summary>Query Azure Monitor Log Analytics data.</summary>
/// <remarks>
/// This command group contains Log Analytics query operations.
/// Use the query subcommand to execute Kusto queries against workspaces or resource IDs.
/// </remarks>
public partial class MonitorLogAnalyticsCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "log-analytics";
    public override string[] Aliases => ["logs"];

    public readonly MonitorLogAnalyticsQueryCommandDef Query = new(auth);
}
