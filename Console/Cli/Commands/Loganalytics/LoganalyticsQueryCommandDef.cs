using Console.Cli.Shared;

namespace Console.Cli.Commands.Generated;

/// <summary>Extension adding KQL query capability to the loganalytics command group.</summary>
public partial class LoganalyticsCommandDef
{
    public readonly LoganalyticsKqlQueryCommandDef Query = new(auth);
}
