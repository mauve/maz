using Azure.Core;
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
/// </summary>
public partial class SearchServiceOptionPack
    : DataplaneResourceOptionPack<SearchServiceResource, Uri>
{
    public override string ArmResourceType => "Microsoft.Search/searchServices";
    public override string HelpTitle => "Search Service";

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
        string resolvedSubscriptionId,
        string resolvedResourceGroupName,
        string resourceName,
        CancellationToken ct
    )
    {
        var sub = armClient.GetSubscriptionResource(
            new ResourceIdentifier($"/subscriptions/{resolvedSubscriptionId}")
        );
        var rg = await sub.GetResourceGroupAsync(resolvedResourceGroupName, ct);
        return await rg.Value.GetSearchServiceAsync(resourceName, cancellationToken: ct);
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
