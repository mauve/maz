using System.Text.Json;
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

    [CliOption(
        Description = "Output results as JSON Lines (one JSON object per row).",
        Required = false,
        Aliases = ["--jsonl"]
    )]
    public bool OutputJsonl { get; set; } = false;

    [CliOption(
        Description = "Output results as a JSON array.",
        Required = false,
        Aliases = ["--jsonarray"]
    )]
    public bool OutputJsonArray { get; set; } = false;

    [CliOption(
        Description = "Continuously poll the workspace for new results at the specified interval in seconds.",
        Required = false
    )]
    public int? Tail { get; set; }

    [CliOption(
        Description = "The timestamp column to use for tail queries. Defaults to TimeGenerated or ts_t if available.",
        Required = false,
        Aliases = ["--tail-column"]
    )]
    public string? TailTimestampColumn { get; set; }

    public required MonitorLogAnalyticsCommand Parent { get; set; }

    public LogsQueryClient CreateClient() => new(Parent.Parent.Parent.Credential);

    private async Task<LogsQueryResult> ExecuteQuery(
        LogsQueryClient client,
        QueryTimeRange timeRange,
        CancellationToken cancellationToken
    )
    {
        if (WorkspaceId != null)
        {
            return await client.QueryWorkspaceAsync(
                WorkspaceId.ToString(),
                Query,
                timeRange,
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
                timeRange,
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

    private static string? ResolveTimestampColumn(LogsTable table, string? userColumn)
    {
        if (userColumn != null)
        {
            return table.Columns.Any(c => c.Name == userColumn) ? userColumn : null;
        }

        string[] defaults = ["TimeGenerated", "ts_t"];
        foreach (var name in defaults)
        {
            if (table.Columns.Any(c => c.Name == name))
            {
                return name;
            }
        }

        return null;
    }

    private static DateTimeOffset? GetLatestTimestamp(LogsTable table, string columnName)
    {
        int colIndex = -1;
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].Name == columnName)
            {
                colIndex = i;
                break;
            }
        }

        if (colIndex < 0)
        {
            return null;
        }

        DateTimeOffset? latest = null;
        foreach (var row in table.Rows)
        {
            if (row[colIndex] is DateTimeOffset dt && (latest == null || dt > latest))
            {
                latest = dt;
            }
        }
        return latest;
    }

    public async Task RunAsync(CliContext context)
    {
        var client = CreateClient();
        QueryTimeRange timeRange = QueryTimeRange.All;

        do
        {
            LogsQueryResult result = await ExecuteQuery(client, timeRange, context.CancellationToken);

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
                    await visualization
                        .ToStream()
                        .CopyToAsync(fileStream, context.CancellationToken);
                    context.Error.WriteLine($"Visualization saved to {VisualizationOutput}");
                }
                else
                {
                    context.Error.WriteLine("No visualization data available.");
                }
            }

            var table = result.Table;
            if (OutputJsonl || OutputJsonArray)
            {
                var columns = table.Columns;
                var rows = new List<Dictionary<string, object?>>();
                foreach (var row in table.Rows)
                {
                    var obj = new Dictionary<string, object?>();
                    for (int i = 0; i < columns.Count; i++)
                    {
                        obj[columns[i].Name] = row[i] switch
                        {
                            DateTimeOffset dt => dt.ToString("o"),
                            TimeSpan ts => ts.ToString("o"),
                            _ => row[i],
                        };
                    }
                    rows.Add(obj);
                }

                if (OutputJsonArray)
                {
                    context.Output.WriteLine(JsonSerializer.Serialize(rows));
                }
                else
                {
                    foreach (var obj in rows)
                    {
                        context.Output.WriteLine(JsonSerializer.Serialize(obj));
                    }
                }
            }
            else
            {
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

            if (Tail != null)
            {
                var tsColumn = ResolveTimestampColumn(result.Table, TailTimestampColumn);
                if (tsColumn == null)
                {
                    throw new InvocationException(
                        "No suitable timestamp column found in query results. "
                        + "Use --tail-timestamp-column to specify the column to use."
                    );
                }

                var latest = GetLatestTimestamp(result.Table, tsColumn);
                if (latest != null)
                {
                    timeRange = new QueryTimeRange(latest.Value, DateTimeOffset.UtcNow);
                }

                if (Tail.Value > 2)
                {
                    await WaitWithThrobber(
                        TimeSpan.FromSeconds(Tail.Value),
                        context.Error,
                        context.CancellationToken
                    );
                }
                else
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Tail.Value),
                        context.CancellationToken
                    );
                }
            }
        } while (Tail != null && !context.CancellationToken.IsCancellationRequested);
    }

    private static async Task WaitWithThrobber(
        TimeSpan duration,
        TextWriter stderr,
        CancellationToken cancellationToken
    )
    {
        char[] frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
        int frame = 0;
        var end = DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < end && !cancellationToken.IsCancellationRequested)
        {
            var remaining = end - DateTimeOffset.UtcNow;
            stderr.Write($"\r{frames[frame]} waiting {remaining.TotalSeconds:F0}s ");
            frame = (frame + 1) % frames.Length;
            await Task.Delay(100, cancellationToken);
        }

        stderr.Write("\r\x1b[2K");
    }
}
