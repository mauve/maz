using Azure.Identity;
using Azure.Monitor.Query;
using Azure.ResourceManager;
using Console.Cli.Http;
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

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    /// <summary>The workspace customerId GUID, name, or hierarchical ref (rg/name) to explore.</summary>
    [CliOption(
        "--workspace-id",
        "--workspace",
        CompletionProviderType = typeof(LogAnalyticsWorkspaceCompletionProvider),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? WorkspaceId { get; }

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
                "The explore command requires an interactive terminal. "
                    + "Output appears to be redirected — use 'maz loganalytics query' instead."
            );

        if (WorkspaceId is null && ResourceId is null)
            throw new InvocationException(
                "Either --workspace-id or --resource-id must be specified."
            );

        var credential = _auth.GetCredential();
        var armClient = new ArmClient(credential);

        string? resolvedWorkspaceId = null;
        string? workspaceArmId = null;
        if (WorkspaceId is not null)
        {
            if (Guid.TryParse(WorkspaceId, out _))
            {
                resolvedWorkspaceId = WorkspaceId;
            }
            else
            {
                (resolvedWorkspaceId, workspaceArmId) = await ResolveWorkspaceCustomerIdAsync(
                    WorkspaceId,
                    armClient,
                    ct
                );
            }
        }

        string? resolvedResourceId = null;
        if (ResourceId is not null)
        {
            resolvedResourceId = ResourceId.StartsWith(
                "/subscriptions/",
                StringComparison.OrdinalIgnoreCase
            )
                ? ResourceId
                : await ResolveResourceArmIdAsync(ResourceId, armClient, ct);
            workspaceArmId ??= resolvedResourceId;
        }

        var client = new LogsQueryClient(credential);
        await using var app = new KustoTuiApp(
            client,
            resolvedWorkspaceId,
            resolvedResourceId,
            InitialQuery,
            HistorySize,
            credential,
            workspaceArmId
        );
        await app.RunAsync(ct);
        return 0;
    }

    private async Task<(string customerId, string armPath)> ResolveWorkspaceCustomerIdAsync(
        string workspaceRef,
        ArmClient armClient,
        CancellationToken ct
    )
    {
        var (sub, rg, name) = await ResourceNameResolver.ResolveAsync(
            workspaceRef,
            ResourceGroup,
            armClient,
            "Microsoft.OperationalInsights/workspaces",
            ct
        );

        var restClient = new AzureRestClient(_auth.GetCredential());
        var path =
            $"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{name}";
        var json = await restClient.SendAsync(HttpMethod.Get, path, "2025-07-01", null, ct);
        var customerId = json?["properties"]?["customerId"]?.GetValue<string>();

        if (customerId is null)
            throw new InvocationException(
                $"Could not read customerId for workspace '{name}' in resource group '{rg}'."
            );

        return (customerId, path);
    }

    private async Task<string> ResolveResourceArmIdAsync(
        string resourceRef,
        ArmClient armClient,
        CancellationToken ct
    )
    {
        var parsed = ResourceIdentifierParser.Parse(resourceRef);

        var effectiveSub = parsed.SubscriptionSegment is not null
            ? ResourceIdentifierParser.NormalizeSubscriptionSegment(parsed.SubscriptionSegment)
            : ResourceGroup.Subscription.SubscriptionId
                ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");

        var effectiveRg = parsed.ResourceGroupSegment is not null
            ? ResourceIdentifierParser.NormalizeResourceGroupSegment(parsed.ResourceGroupSegment)
            : ResourceGroup.ResourceGroupName
                ?? Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");

        var name = parsed.ResourceNameSegment;

        var sub = await SubscriptionOptionPack.ResolveAsync(armClient, effectiveSub);

        var filter = effectiveRg is not null
            ? $"name eq '{name}' and resourceGroup eq '{effectiveRg}'"
            : $"name eq '{name}'";

        var matches = new List<string>();
        await foreach (
            var resource in sub.GetGenericResourcesAsync(filter: filter, cancellationToken: ct)
        )
        {
            if (resource.Id is not null)
                matches.Add(resource.Id.ToString());
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"Resource '{name}' not found in subscription '{sub.Data?.DisplayName ?? sub.Id.Name}'."
            ),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} resources:\n"
                    + string.Join("\n", matches.Select(id => $"  {id}"))
            ),
        };
    }
}

internal sealed class LogAnalyticsWorkspaceCompletionProvider : ICliCompletionProvider
{
    public async ValueTask<IEnumerable<string>> GetCompletionsAsync(CliCompletionContext context)
    {
        var auth = context.GetOptionPack<AuthOptionPack>();
        var credential = auth?.GetCredential() ?? new DefaultAzureCredential();
        var armClient = new ArmClient(credential);
        var word = context.WordToComplete;

        string? subHint = null;
        string? rgHint = null;
        string prefix = word;
        string headPfx = "";

        if (word.Contains('/'))
        {
            var lastSlash = word.LastIndexOf('/');
            var head = word[..lastSlash];
            prefix = word[(lastSlash + 1)..];
            headPfx = word[..(lastSlash + 1)];

            try
            {
                var p = ResourceIdentifierParser.Parse(head + "/placeholder");
                subHint = ResourceIdentifierParser.NormalizeSubscriptionSegment(
                    p.SubscriptionSegment
                );
                rgHint = ResourceIdentifierParser.NormalizeResourceGroupSegment(
                    p.ResourceGroupSegment
                );
            }
            catch
            {
                // Unparseable partial — fall through to pack-level hints.
            }
        }

        subHint ??= context.GetOptionPack<SubscriptionOptionPack>()?.SubscriptionId;
        rgHint ??= context.GetOptionPack<ResourceGroupOptionPack>()?.ResourceGroupName;

        try
        {
            var sub = await SubscriptionOptionPack.ResolveAsync(armClient, subHint);

            var filter = "resourceType eq 'Microsoft.OperationalInsights/workspaces'";
            if (rgHint is not null)
                filter += $" and resourceGroup eq '{rgHint}'";

            var results = new List<string>();
            await foreach (var resource in sub.GetGenericResourcesAsync(filter: filter))
            {
                var name = resource.Data?.Name;
                if (name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(headPfx + name);
            }
            return results;
        }
        catch
        {
            return [];
        }
    }
}
