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
///   /pv/account-name
/// </summary>
public partial class PurviewOptionPack : DataplaneResourceOptionPack<PurviewAccountResource, Uri>
{
    public const string ShortPathPrefix = "/pv/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;
    public override string HelpTitle => "Purview";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Purview account name, or combined format: [sub/]rg/account-name.</summary>
    [CliOption(
        "--purview",
        "--pv",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<PurviewOptionPack, PurviewAccountResource>),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? AccountName { get; }

    protected override string? RawResourceValue => AccountName;

    protected override Uri GetDataplaneRef(PurviewAccountResource resource) =>
        new Uri(resource.Data.Endpoints.Catalog!);

    protected override async Task<PurviewAccountResource> GetResourceCoreAsync(
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
            return await rg.Value.GetPurviewAccountAsync(name, ct);
        }

        var matches = new List<PurviewAccountResource>();
        await foreach (var account in sub.GetPurviewAccountsAsync(cancellationToken: ct))
        {
            if (account.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(account);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException($"Purview account '{name}' not found in subscription."),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} accounts:\n"
                    + string.Join("\n", matches.Select(m =>
                        $"  {m.Data.Name}  (resource-group: {m.Id?.ResourceGroupName ?? "?"})"))
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
            await foreach (var a in rg.Value.GetPurviewAccounts().GetAllAsync(cancellationToken: ct))
            {
                if (a.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(a.Data.Name);
            }
        }
        else
        {
            await foreach (var a in sub.GetPurviewAccountsAsync(cancellationToken: ct))
            {
                if (a.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(a.Data.Name);
            }
        }

        return results;
    }
}
