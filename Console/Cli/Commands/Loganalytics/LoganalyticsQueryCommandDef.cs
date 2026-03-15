using Console.Cli.Shared;

namespace Console.Cli.Commands.Generated;

/// <summary>Extension adding KQL query and interactive explore capability to the loganalytics command group.</summary>
public partial class LoganalyticsCommandDef
{
    public readonly LoganalyticsKqlQueryCommandDef Query = new(auth);
    public readonly LoganalyticsExploreCommandDef Explore = new(auth);
}
