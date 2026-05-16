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
///   /arm/store-name
///</summary>
public partial class AppConfigurationOptionPack
    : DataplaneResourceOptionPack<AppConfigurationStoreResource, Uri>
{
    public override string HelpTitle => "App Configuration Store";

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
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

    protected override string ResourceType => "Microsoft.AppConfiguration/configurationStores";

    protected override async Task<AppConfigurationStoreResource> GetResourceCoreAsync(
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
        return (await rg.GetAppConfigurationStoreAsync(name, ct)).Value;
    }

    }
