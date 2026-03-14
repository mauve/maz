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
///   /ac/store-name
/// </summary>
public partial class AppConfigurationOptionPack
    : DataplaneResourceOptionPack<AppConfigurationStoreResource, Uri>
{
    public const string ShortPathPrefix = "/ac/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;

    public override string HelpTitle => "App Configuration Store";

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly ResourceGroupOptionPack ResourceGroup = new();

    protected override SubscriptionOptionPack SubscriptionPack => Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

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
            return await rg.Value.GetAppConfigurationStoreAsync(name, ct);
        }

        var matches = new List<AppConfigurationStoreResource>();
        await foreach (
            var store in sub.GetAppConfigurationStoresAsync(cancellationToken: ct)
        )
        {
            if (store.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(store);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"App Configuration store '{name}' not found in subscription."
            ),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} stores:\n"
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
                var store in rg.Value
                    .GetAppConfigurationStores()
                    .GetAllAsync(cancellationToken: ct)
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
