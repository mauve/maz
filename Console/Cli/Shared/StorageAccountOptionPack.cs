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
///   /sa/account-name
/// </summary>
public partial class StorageAccountOptionPack : ArmResourceOptionPack<StorageAccountResource>
{
    public const string ShortPathPrefix = "/sa/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;

    public override string HelpTitle => "Storage Account";

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly ResourceGroupOptionPack ResourceGroup = new();

    protected override SubscriptionOptionPack SubscriptionPack => Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

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
        if (raw.StartsWith(ShortPathPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[ShortPathPrefix.Length..];
        return ResourceIdentifierParser.Parse(raw).ResourceNameSegment;
    }

    protected override async Task<StorageAccountResource> GetResourceCoreAsync(
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
            return await rg.Value.GetStorageAccountAsync(name, cancellationToken: ct);
        }

        // No RG — search all RGs in the subscription
        var matches = new List<StorageAccountResource>();
        await foreach (var sa in sub.GetStorageAccountsAsync(cancellationToken: ct))
        {
            if (sa.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(sa);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"Storage account '{name}' not found in subscription."
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
