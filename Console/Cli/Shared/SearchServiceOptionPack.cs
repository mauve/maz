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
///   /arm/service-name
///</summary>
public partial class SearchServiceOptionPack
    : DataplaneResourceOptionPack<SearchServiceResource, Uri>
{
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

    protected override string ResourceType => "Microsoft.Search/searchServices";

    protected override async Task<SearchServiceResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSub,
        string resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var rgId = new ResourceIdentifier(
            $"/subscriptions/{resolvedSub}/resourceGroups/{resolvedRg}"
        );
        var rg = armClient.GetResourceGroupResource(rgId);
        return (await rg.GetSearchServiceAsync(name, cancellationToken: ct)).Value;
    }

}
