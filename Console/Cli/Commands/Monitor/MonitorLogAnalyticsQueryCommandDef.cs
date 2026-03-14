using System.Text.Json;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Monitor;

/// <summary>Query Azure Monitor Log Analytics workspaces or Azure resource logs.</summary>
/// <remarks>
/// Provide either --workspace-id or --resource-id to select the query target.
/// The command supports streaming tail mode, JSON output options, and optional query statistics.
/// </remarks>
public partial class MonitorLogAnalyticsQueryCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "query";

    /// <summary>The LogAnalytics query to execute.</summary>
    [CliOption("--query", "-q", Required = true)]
    public partial string Query { get; }

    /// <summary>The workspace ID to query against.</summary>
    [CliOption("--workspace-id", "--workspace")]
    public partial Guid? WorkspaceId { get; }

    /// <summary>The resource ID to query against.</summary>
    [CliOption("--resource-id", "--resource")]
    public partial string? ResourceId { get; }

    /// <summary>Save the visualization output to a file.</summary>
    [CliOption("--visualization-output", "--vis-output")]
    public partial string? VisualizationOutput { get; }

    /// <summary>Include query statistics in the output.</summary>
    [CliOption("--include-statistics", "--stats")]
    public partial bool IncludeStatistics { get; }

    /// <summary>Additional workspaces to include in the query.</summary>
    [CliOption("--additional-workspaces")]
    public partial List<string> AdditionalWorkspaces { get; } = [];

    /// <summary>Output results as JSON Lines (one JSON object per row).</summary>
    [CliOption("--output-jsonl", "--jsonl")]
    public partial bool OutputJsonl { get; }

    /// <summary>Output results as a JSON array.</summary>
    [CliOption("--output-json-array", "--jsonarray")]
    public partial bool OutputJsonArray { get; }

    /// <summary>Continuously poll for new results at the specified interval in seconds.</summary>
    [CliOption("--tail")]
    public partial int? Tail { get; }

    /// <summary>The timestamp column for tail queries. Defaults to TimeGenerated or ts_t.</summary>
    [CliOption("--tail-timestamp-column", "--tail-column")]
    public partial string? TailTimestampColumn { get; }

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var client = new LogsQueryClient(_auth.GetCredential());
        QueryTimeRange timeRange = QueryTimeRange.All;

        do
        {
            var result = await ExecuteQuery(client, timeRange, ct);

            var visOutput = VisualizationOutput;
            if (visOutput != null)
            {
                var visualization = result.GetVisualization();
                if (visualization != null)
                {
                    await using var fileStream = new FileStream(
                        visOutput,
                        FileMode.Create,
                        FileAccess.Write
                    );
                    await visualization.ToStream().CopyToAsync(fileStream, ct);
                    System.Console.Error.WriteLine($"Visualization saved to {visOutput}");
                }
                else
                {
                    System.Console.Error.WriteLine("No visualization data available.");
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
                    System.Console.WriteLine(JsonSerializer.Serialize(rows));
                else
                    foreach (var obj in rows)
                        System.Console.WriteLine(JsonSerializer.Serialize(obj));
            }
            else
            {
                foreach (var row in table.Rows)
                {
                    System.Console.WriteLine(
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
                    System.Console.Error.WriteLine("Query Statistics:");
                    System.Console.Error.WriteLine(stats.ToString());
                }
                else
                {
                    System.Console.Error.WriteLine("No statistics data available.");
                }
            }

            var tail = Tail;
            if (tail != null)
            {
                var tsColumn = ResolveTimestampColumn(result.Table, TailTimestampColumn);
                if (tsColumn == null)
                    throw new InvocationException(
                        "No suitable timestamp column found. Use --tail-timestamp-column to specify the column."
                    );

                var latest = GetLatestTimestamp(result.Table, tsColumn);
                if (latest != null)
                    timeRange = new QueryTimeRange(latest.Value, DateTimeOffset.UtcNow);

                if (tail.Value > 2)
                    await WaitWithThrobber(TimeSpan.FromSeconds(tail.Value), ct);
                else
                    await Task.Delay(TimeSpan.FromSeconds(tail.Value), ct);
            }
        } while (Tail != null && !ct.IsCancellationRequested);

        return 0;
    }

    private async Task<LogsQueryResult> ExecuteQuery(
        LogsQueryClient client,
        QueryTimeRange timeRange,
        CancellationToken ct
    )
    {
        var queryText = Query;
        var opts = new LogsQueryOptions
        {
            IncludeStatistics = IncludeStatistics,
            IncludeVisualization = VisualizationOutput != null,
        };

        var workspaceId = WorkspaceId;
        var resourceId = ResourceId;

        if (workspaceId != null)
            return await client.QueryWorkspaceAsync(
                workspaceId.ToString(),
                queryText,
                timeRange,
                opts,
                ct
            );

        if (resourceId != null)
            return await client.QueryResourceAsync(new(resourceId), queryText, timeRange, opts, ct);

        throw new InvocationException("Either --workspace-id or --resource-id must be specified.");
    }

    private static string? ResolveTimestampColumn(LogsTable table, string? userColumn)
    {
        if (userColumn != null)
            return table.Columns.Any(c => c.Name == userColumn) ? userColumn : null;

        foreach (var name in new[] { "TimeGenerated", "ts_t" })
            if (table.Columns.Any(c => c.Name == name))
                return name;

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
            return null;

        DateTimeOffset? latest = null;
        foreach (var row in table.Rows)
        {
            if (row[colIndex] is DateTimeOffset dt && (latest == null || dt > latest))
                latest = dt;
        }
        return latest;
    }

    private static async Task WaitWithThrobber(TimeSpan duration, CancellationToken ct)
    {
        char[] frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
        int frame = 0;
        var end = DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < end && !ct.IsCancellationRequested)
        {
            var remaining = end - DateTimeOffset.UtcNow;
            System.Console.Error.Write($"\r{frames[frame]} waiting {remaining.TotalSeconds:F0}s ");
            frame = (frame + 1) % frames.Length;
            await Task.Delay(100, ct);
        }

        System.Console.Error.Write("\r\x1b[2K");
    }
}
