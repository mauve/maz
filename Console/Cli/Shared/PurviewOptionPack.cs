using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Purview;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Purview (Microsoft Purview) account by name.
///
/// Accepted formats for --purview / --pv:
///   account-name
///   rg/account-name
///   sub/rg/account-name
///   /arm/account-name
///</summary>
public partial class PurviewOptionPack : DataplaneResourceOptionPack<PurviewAccountResource, Uri>
{
    public override string HelpTitle => "Purview";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Purview account name, or combined format: [sub/]rg/account-name.</summary>
    [CliOption(
        "--purview",
        "--pv",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            PurviewOptionPack,
            PurviewAccountResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? AccountName { get; }

    protected override string? RawResourceValue => AccountName;

    protected override Uri GetDataplaneRef(PurviewAccountResource resource) =>
        new Uri(resource.Data.Endpoints.Catalog!);

    protected override string ResourceType => "Microsoft.Purview/accounts";

    protected override async Task<PurviewAccountResource> GetResourceCoreAsync(
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
        return (await rg.GetPurviewAccountAsync(name, ct)).Value;
    }

}
