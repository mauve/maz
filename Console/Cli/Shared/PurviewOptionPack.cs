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
/// </summary>
public partial class PurviewOptionPack : DataplaneResourceOptionPack<PurviewAccountResource, Uri>
{
    public override string ArmResourceType => "Microsoft.Purview/accounts";
    public override string HelpTitle => "Purview";

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

    protected override async Task<PurviewAccountResource> GetResourceCoreAsync(
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
        return await rg.Value.GetPurviewAccountAsync(resourceName, ct);
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
                var a in rg.Value.GetPurviewAccounts().GetAllAsync(cancellationToken: ct)
            )
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
