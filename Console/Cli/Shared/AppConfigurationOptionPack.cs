using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppConfiguration;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure App Configuration store by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --appconfig / --ac:
///   store-name
///   rg/store-name
///   sub/rg/store-name
///   /s/{sub}/rg/store-name
///   /subscriptions/{guid}/rg/store-name
/// </summary>
public partial class AppConfigurationOptionPack
    : DataplaneResourceOptionPack<AppConfigurationStoreResource, Uri>
{
    public override string ArmResourceType => "Microsoft.AppConfiguration/configurationStores";
    public override string HelpTitle => "App Configuration Store";

    /// <summary>
    /// App Configuration store name, or combined format: [sub/]rg/store-name (see section description).
    /// </summary>
    [CliOption(
        "--appconfig",
        "--ac",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            AppConfigurationOptionPack,
            AppConfigurationStoreResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? AppConfigName { get; }

    protected override string? RawResourceValue => AppConfigName;

    protected override Uri GetDataplaneRef(AppConfigurationStoreResource resource) =>
        new(resource.Data.Endpoint!);

    protected override async Task<AppConfigurationStoreResource> GetResourceCoreAsync(
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
        return await rg.Value.GetAppConfigurationStoreAsync(resourceName, ct);
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
                var store in rg.Value.GetAppConfigurationStores().GetAllAsync(cancellationToken: ct)
            )
            {
                if (store.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(store.Data.Name);
            }
        }
        else
        {
            await foreach (var store in sub.GetAppConfigurationStoresAsync(cancellationToken: ct))
            {
                if (store.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(store.Data.Name);
            }
        }

        return results;
    }
}
