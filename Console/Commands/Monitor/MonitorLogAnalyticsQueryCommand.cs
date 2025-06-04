using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using DotMake.CommandLine;

namespace Console.Commands.Monitor;

[CliCommand(
    Description = "Query Azure Monitor Log Analytics workspaces or Azure resource logs.",
    Parent = typeof(MonitorLogAnalyticsCommand),
    ShortFormAutoGenerate = true,
    Name = "query"
)]
public class MonitorLogAnalyticsQueryCommand
{
    [CliOption(
        Description = "The LogAnalytics query to execute.",
        Arity = CliArgumentArity.ExactlyOne
    )]
    public required string Query { get; set; }

    [CliOption(
        Description = "The workspace ID to query against.",
        Aliases = ["--workspace"],
        Required = false
    )]
    public Guid? WorkspaceId { get; set; }

    [CliOption(
        Description = "The resource ID to query against.",
        Aliases = ["--resource"],
        Required = false
    )]
    public string? ResourceId { get; set; }

    [CliOption(
        Description = "Save the visualization output to a file.",
        Required = false,
        Aliases = ["--vis-output"]
    )]
    public string? VisualizationOutput { get; set; }

    [CliOption(
        Description = "Include query statistics in the output.",
        Required = false,
        Aliases = ["--stats"]
    )]
    public bool IncludeStatistics { get; set; } = false;

    [CliOption(Description = "Additional workspaces to include in the query.", Required = false)]
    public List<string> AdditionalWorkspaces { get; set; } = [];

    public required MonitorLogAnalyticsCommand Parent { get; set; }

    public LogsQueryClient CreateClient() => new(Parent.Parent.Parent.Credential);

    private async Task<LogsQueryResult> ExecuteQuery(
        LogsQueryClient client,
        CancellationToken cancellationToken
    )
    {
        if (WorkspaceId != null)
        {
            return await client.QueryWorkspaceAsync(
                WorkspaceId.ToString(),
                Query,
                QueryTimeRange.All,
                new LogsQueryOptions
                {
                    IncludeStatistics = IncludeStatistics,
                    IncludeVisualization = !string.IsNullOrEmpty(VisualizationOutput),
                },
                cancellationToken
            );
        }
        else if (ResourceId != null)
        {
            return await client.QueryResourceAsync(
                new(ResourceId),
                Query,
                QueryTimeRange.All,
                new LogsQueryOptions
                {
                    IncludeStatistics = IncludeStatistics,
                    IncludeVisualization = !string.IsNullOrEmpty(VisualizationOutput),
                },
                cancellationToken
            );
        }
        else
        {
            throw new InvocationException(
                "Either --workspace-id or --resource-id must be specified for the query."
            );
        }
    }

    public async Task RunAsync(CliContext context)
    {
        LogsQueryResult result = await ExecuteQuery(CreateClient(), context.CancellationToken);

        if (VisualizationOutput != null)
        {
            var visualization = result.GetVisualization();
            if (visualization != null)
            {
                await using var fileStream = new FileStream(
                    VisualizationOutput,
                    FileMode.Create,
                    FileAccess.Write
                );
                await visualization.ToStream().CopyToAsync(fileStream, context.CancellationToken);
                context.Error.WriteLine($"Visualization saved to {VisualizationOutput}");
            }
            else
            {
                context.Error.WriteLine("No visualization data available.");
            }
        }

        var table = result.Table;
        foreach (var row in table.Rows)
        {
            context.Output.WriteLine(
                string.Join(
                    "\t",
                    row.Select(v =>
                        v switch
                        {
                            DateTimeOffset dt => dt.ToString("o"),
                            TimeSpan ts => ts.ToString("o"),
                            _ => v?.ToString() ?? "null",
                        }
                    )
                )
            );
        }

        if (IncludeStatistics)
        {
            var stats = result.GetStatistics();
            if (stats != null)
            {
                context.Error.WriteLine("Query Statistics:");
                context.Error.WriteLine(stats.ToString());
            }
            else
            {
                context.Error.WriteLine("No statistics data available.");
            }
        }
    }
}
