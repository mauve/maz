using Azure.Core;
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
/// </summary>
public partial class BatchAccountOptionPack : DataplaneResourceOptionPack<BatchAccountResource, Uri>
{
    public override string ArmResourceType => "Microsoft.Batch/batchAccounts";
    public override string HelpTitle => "Batch Account";

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
        return await rg.Value.GetBatchAccountAsync(resourceName, ct);
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
