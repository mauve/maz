using Azure.Monitor.Query;
using Console.Cli.Shared;
using Console.Tui;

namespace Console.Cli.Commands.Generated;

/// <summary>Launch an interactive KQL explorer TUI for a Log Analytics workspace or resource.</summary>
/// <remarks>
/// Provide either --workspace-id or --resource-id to select the query target.
/// Requires an interactive terminal; use 'maz loganalytics query' for non-interactive scenarios.
/// </remarks>
public partial class LoganalyticsExploreCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "explore";
    public override string[] Aliases => ["ex"];

    /// <summary>The workspace ID to explore.</summary>
    [CliOption("--workspace-id", "--workspace")]
    public partial Guid? WorkspaceId { get; }

    /// <summary>The resource ID to explore.</summary>
    [CliOption("--resource-id", "--resource")]
    public partial string? ResourceId { get; }

    /// <summary>Pre-load the editor with this KQL query.</summary>
    [CliOption("--query", "-q")]
    public partial string? InitialQuery { get; }

    /// <summary>Number of query executions to keep in session history (default: 100).</summary>
    [CliOption("--history-size")]
    public partial int HistorySize { get; } = 100;

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!InteractiveOptionPack.IsEffectivelyInteractive(true))
            throw new InvocationException(
                "The explore command requires an interactive terminal. " +
                "Output appears to be redirected — use 'maz loganalytics query' instead.");

        if (WorkspaceId is null && ResourceId is null)
            throw new InvocationException(
                "Either --workspace-id or --resource-id must be specified.");

        var client = new LogsQueryClient(_auth.GetCredential());
        await using var app = new KustoTuiApp(
            client,
            WorkspaceId?.ToString(),
            ResourceId,
            InitialQuery,
            HistorySize);
        await app.RunAsync(ct);
        return 0;
    }
}
