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
///   /cl/ledger-name
/// </summary>
public partial class ConfidentialLedgerOptionPack
    : DataplaneResourceOptionPack<ConfidentialLedgerResource, Uri>
{
    public const string ShortPathPrefix = "/cl/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;
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

    protected override async Task<ConfidentialLedgerResource> GetResourceCoreAsync(
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
            return await rg.Value.GetConfidentialLedgerAsync(name, ct);
        }

        var matches = new List<ConfidentialLedgerResource>();
        await foreach (var ledger in sub.GetConfidentialLedgersAsync(cancellationToken: ct))
        {
            if (ledger.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(ledger);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"Confidential Ledger '{name}' not found in subscription."
            ),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} ledgers:\n"
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
