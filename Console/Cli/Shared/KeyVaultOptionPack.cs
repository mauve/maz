using Azure.ResourceManager;
using Azure.ResourceManager.KeyVault;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Key Vault by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --keyvault / --kv:
///   vault-name
///   rg/vault-name
///   sub/rg/vault-name
///   /s/{sub}/rg/vault-name
///   /subscriptions/{guid}/rg/vault-name
///   /kv/vault-name
/// </summary>
public partial class KeyVaultOptionPack : DataplaneResourceOptionPack<KeyVaultResource, Uri>
{
    // -----------------------------------------------------------------------
    // Short-path prefix
    // -----------------------------------------------------------------------

    public const string ShortPathPrefix = "/kv/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;

    // -----------------------------------------------------------------------
    // Help
    // -----------------------------------------------------------------------

    public override string HelpTitle => "Key Vault";

    // -----------------------------------------------------------------------
    // Child packs — declared HERE so the generator can see them
    // -----------------------------------------------------------------------

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    // -----------------------------------------------------------------------
    // The option itself
    // -----------------------------------------------------------------------

    /// <summary>
    /// Key Vault name, or combined format: [sub/]rg/vault-name (see section description).
    /// </summary>
    [CliOption(
        "--keyvault",
        "--kv",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            KeyVaultOptionPack,
            KeyVaultResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? VaultName { get; }

    protected override string? RawResourceValue => VaultName;

    // -----------------------------------------------------------------------
    // Dataplane
    // -----------------------------------------------------------------------

    protected override Uri GetDataplaneRef(KeyVaultResource resource) =>
        new(resource.Data.Properties.VaultUri!.ToString());

    // -----------------------------------------------------------------------
    // ARM resolution
    // -----------------------------------------------------------------------

    protected override async Task<KeyVaultResource> GetResourceCoreAsync(
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
            return await rg.Value.GetKeyVaultAsync(name, ct);
        }

        // No resource group specified — search all RGs in the subscription.
        var matches = new List<KeyVaultResource>();
        await foreach (var kv in sub.GetKeyVaultsAsync(cancellationToken: ct))
        {
            if (kv.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(kv);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException($"Key Vault '{name}' not found in subscription."),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} vaults:\n"
                    + string.Join(
                        "\n",
                        matches.Select(m =>
                            $"  {m.Data.Name}  (resource-group: {m.Id?.ResourceGroupName ?? "?"})"
                        )
                    )
            ),
        };
    }

    // -----------------------------------------------------------------------
    // Completion
    // -----------------------------------------------------------------------

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
            await foreach (var kv in rg.Value.GetKeyVaults().GetAllAsync(cancellationToken: ct))
            {
                if (kv.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(kv.Data.Name);
            }
        }
        else
        {
            await foreach (var kv in sub.GetKeyVaultsAsync(cancellationToken: ct))
            {
                if (kv.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(kv.Data.Name);
            }
        }

        return results;
    }
}
