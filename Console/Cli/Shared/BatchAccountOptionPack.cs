using Azure.ResourceManager;
using Azure.ResourceManager.Batch;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Batch account by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --batch-account / --ba:
///   account-name
///   rg/account-name
///   sub/rg/account-name
///   /s/{sub}/rg/account-name
///   /subscriptions/{guid}/rg/account-name
///   /ba/account-name
/// </summary>
public partial class BatchAccountOptionPack : DataplaneResourceOptionPack<BatchAccountResource, Uri>
{
    public const string ShortPathPrefix = "/ba/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;

    public override string HelpTitle => "Batch Account";

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>
    /// Batch account name, or combined format: [sub/]rg/account-name (see section description).
    /// </summary>
    [CliOption(
        "--batch-account",
        "--ba",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            BatchAccountOptionPack,
            BatchAccountResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? BatchAccountName { get; }

    protected override string? RawResourceValue => BatchAccountName;

    protected override Uri GetDataplaneRef(BatchAccountResource resource) =>
        new($"https://{resource.Data.AccountEndpoint}");

    protected override async Task<BatchAccountResource> GetResourceCoreAsync(
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
            return await rg.Value.GetBatchAccountAsync(name, ct);
        }

        var matches = new List<BatchAccountResource>();
        await foreach (var account in sub.GetBatchAccountsAsync(cancellationToken: ct))
        {
            if (account.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(account);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"Batch account '{name}' not found in subscription."
            ),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} accounts:\n"
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
                var account in rg.Value.GetBatchAccounts().GetAllAsync(cancellationToken: ct)
            )
            {
                if (account.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(account.Data.Name);
            }
        }
        else
        {
            await foreach (var account in sub.GetBatchAccountsAsync(cancellationToken: ct))
            {
                if (account.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(account.Data.Name);
            }
        }

        return results;
    }
}
