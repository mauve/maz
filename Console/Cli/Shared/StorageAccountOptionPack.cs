using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Storage;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Storage Account by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --storage-account / --sa:
///   account-name
///   rg/account-name
///   sub/rg/account-name
///   /s/{sub}/rg/account-name
///   /subscriptions/{guid}/rg/account-name
/// </summary>
public partial class StorageAccountOptionPack : ArmResourceOptionPack<StorageAccountResource>
{
    public override string ArmResourceType => "Microsoft.Storage/storageAccounts";
    public override string HelpTitle => "Storage Account";

    /// <summary>
    /// Storage account name, or combined format: [sub/]rg/name (see section description).
    /// </summary>
    [CliOption(
        "--storage-account",
        "--sa",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            StorageAccountOptionPack,
            StorageAccountResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? StorageAccountName { get; }

    protected override string? RawResourceValue => StorageAccountName;

    /// <summary>Returns the account name segment without resolving ARM.</summary>
    public string RequireAccountName()
    {
        var raw =
            StorageAccountName ?? throw new InvocationException("--storage-account is required.");
        return ResourceIdentifierParser.Parse(raw).ResourceNameSegment;
    }

    protected override async Task<StorageAccountResource> GetResourceCoreAsync(
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
        return await rg.Value.GetStorageAccountAsync(resourceName, cancellationToken: ct);
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
                var sa in rg.Value.GetStorageAccounts().GetAllAsync(cancellationToken: ct)
            )
            {
                if (sa.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(sa.Data.Name);
            }
        }
        else
        {
            await foreach (var sa in sub.GetStorageAccountsAsync(cancellationToken: ct))
            {
                if (sa.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(sa.Data.Name);
            }
        }

        return results;
    }
}
