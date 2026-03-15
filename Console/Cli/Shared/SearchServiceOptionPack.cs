using Azure.ResourceManager;
using Azure.ResourceManager.Search;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure AI Search service by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --search-service / --ss:
///   service-name
///   rg/service-name
///   sub/rg/service-name
///   /s/{sub}/rg/service-name
///   /subscriptions/{guid}/rg/service-name
///   /ss/service-name
/// </summary>
public partial class SearchServiceOptionPack
    : DataplaneResourceOptionPack<SearchServiceResource, Uri>
{
    public const string ShortPathPrefix = "/ss/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;

    public override string HelpTitle => "Search Service";

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>
    /// Search service name, or combined format: [sub/]rg/service-name (see section description).
    /// </summary>
    [CliOption(
        "--search-service",
        "--ss",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            SearchServiceOptionPack,
            SearchServiceResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? SearchServiceName { get; }

    protected override string? RawResourceValue => SearchServiceName;

    protected override Uri GetDataplaneRef(SearchServiceResource resource) =>
        new($"https://{resource.Data.Name}.search.windows.net");

    protected override async Task<SearchServiceResource> GetResourceCoreAsync(
        ArmClient armClient,
        string? resolvedSub,
        string? resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var sub = await ResolveSubscriptionAsync(armClient, resolvedSub);

        if (resolvedRg is not null)
        {
            var rg = await sub.GetResourceGroupAsync(resolvedRg, ct);
            return await rg.Value.GetSearchServiceAsync(name, cancellationToken: ct);
        }

        var matches = new List<SearchServiceResource>();
        await foreach (var svc in sub.GetSearchServicesAsync(cancellationToken: ct))
        {
            if (svc.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(svc);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"Search service '{name}' not found in subscription."
            ),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} services:\n"
                    + string.Join(
                        "\n",
                        matches.Select(m =>
                            $"  {m.Data.Name}  (resource-group: {m.Id?.ResourceGroupName ?? "?"})"
                        )
                    )
            ),
        };
    }

    public override async Task<IEnumerable<string>> GetCompletionCandidatesAsync(
        ArmClient armClient,
        string? subHint,
        string? rgHint,
        string prefix,
        CancellationToken ct = default
    )
    {
        var sub = await ResolveSubscriptionAsync(armClient, subHint);
        var results = new List<string>();

        if (rgHint is not null)
        {
            var rg = await sub.GetResourceGroupAsync(rgHint, ct);
            await foreach (
                var svc in rg.Value.GetSearchServices().GetAllAsync(cancellationToken: ct)
            )
            {
                if (svc.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(svc.Data.Name);
            }
        }
        else
        {
            await foreach (var svc in sub.GetSearchServicesAsync(cancellationToken: ct))
            {
                if (svc.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(svc.Data.Name);
            }
        }

        return results;
    }
}
