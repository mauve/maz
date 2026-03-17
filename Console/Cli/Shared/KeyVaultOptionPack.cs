using Azure.Core;
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
///   https://vault-name.vault.azure.net  (direct dataplane URI)
///   /arm/vault-name  (force ARM lookup, bypassing direct URI handling)
/// </summary>
public partial class KeyVaultOptionPack : DataplaneResourceOptionPack<KeyVaultResource, Uri>
{
    public override string ArmResourceType => "Microsoft.KeyVault/vaults";
    public override string HelpTitle => "Key Vault";

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

    /// <summary>
    /// GAP-6: Accept https:// vault URIs directly without ARM lookup.
    /// </summary>
    protected override bool TryParseDirectDataplaneRef(string raw, out Uri? result)
    {
        if (
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && raw.Contains(".vault.azure.net", StringComparison.OrdinalIgnoreCase)
        )
        {
            result = new Uri(raw);
            return true;
        }
        result = null;
        return false;
    }

    // -----------------------------------------------------------------------
    // ARM resolution
    // -----------------------------------------------------------------------

    protected override async Task<KeyVaultResource> GetResourceCoreAsync(
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
        return await rg.Value.GetKeyVaultAsync(resourceName, ct);
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
