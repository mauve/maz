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
/// </summary>
public partial class ConfidentialLedgerOptionPack
    : DataplaneResourceOptionPack<ConfidentialLedgerResource, Uri>
{
    public override string ArmResourceType => "Microsoft.ConfidentialLedger/ledgers";
    public override string HelpTitle => "Confidential Ledger";

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

    protected override async Task<ConfidentialLedgerResource> GetResourceCoreAsync(
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
        return await rg.Value.GetConfidentialLedgerAsync(resourceName, ct);
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
                var l in rg.Value.GetConfidentialLedgers().GetAllAsync(cancellationToken: ct)
            )
            {
                if (l.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(l.Data.Name);
            }
        }
        else
        {
            await foreach (var l in sub.GetConfidentialLedgersAsync(cancellationToken: ct))
            {
                if (l.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(l.Data.Name);
            }
        }

        return results;
    }
}
