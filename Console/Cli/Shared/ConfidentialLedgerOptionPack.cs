using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ConfidentialLedger;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Confidential Ledger by name.
///
/// Accepted formats for --ledger / --cl:
///   ledger-name
///   rg/ledger-name
///   sub/rg/ledger-name
///   /arm/ledger-name
///</summary>
public partial class ConfidentialLedgerOptionPack
    : DataplaneResourceOptionPack<ConfidentialLedgerResource, Uri>
{
    public override string HelpTitle => "Confidential Ledger";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Confidential Ledger name, or combined format: [sub/]rg/ledger-name.</summary>
    [CliOption(
        "--ledger",
        "--cl",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            ConfidentialLedgerOptionPack,
            ConfidentialLedgerResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? LedgerName { get; }

    protected override string? RawResourceValue => LedgerName;

    protected override Uri GetDataplaneRef(ConfidentialLedgerResource resource) =>
        resource.Data.Properties.LedgerUri!;

    protected override string ResourceType => "Microsoft.ConfidentialLedger/ledgers";

    protected override async Task<ConfidentialLedgerResource> GetResourceCoreAsync(
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
        return (await rg.GetConfidentialLedgerAsync(name, ct)).Value;
    }

}
