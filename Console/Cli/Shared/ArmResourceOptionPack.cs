using System.CommandLine;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;

namespace Console.Cli.Shared;

/// <summary>
/// Base class for option packs that target a named ARM resource.
///
/// The <typeparamref name="TResource"/> option value accepts several forms:
///   {name}
///   {rg}/{name}
///   {sub}/{rg}/{name}
///   /s/{sub}/{rg}/{name}
///   /subscriptions/{guid}/{rg}/{name}
///   /subscriptions/{guid}/resourceGroups/{rg}/providers/{ns}/{type}/{name}  (full ARM resource ID)
///   https://portal.azure.com/#...  (portal URL — ARM resource ID extracted automatically)
///
/// Combined subscription/resource-group segments override the standalone
/// --subscription-id / --resource-group options (with a warning).
/// </summary>
public abstract class ArmResourceOptionPack<TResource> : OptionPack
{
    // -----------------------------------------------------------------------
    // Concrete sub/rg fields — shared by all subclasses (no more boilerplate)
    // -----------------------------------------------------------------------

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    // Wire the child pack's options into this pack's command (without the source generator).
    protected override void AddChildPacksTo(Command cmd) =>
        ((OptionPack)ResourceGroup).AddOptionsTo(cmd);

    // -----------------------------------------------------------------------
    // Abstract members — subclass supplies the resource-specific details
    // -----------------------------------------------------------------------

    /// <summary>
    /// The raw string value typed by the user (the [CliOption]-decorated property on the subclass).
    /// </summary>
    protected abstract string? RawResourceValue { get; }

    /// <summary>
    /// The ARM resource type string, e.g. "Microsoft.KeyVault/vaults".
    /// Used for ARG queries when subscription/resource-group are unknown.
    /// </summary>
    public abstract string ArmResourceType { get; }

    // -----------------------------------------------------------------------
    // Help text
    // -----------------------------------------------------------------------

    public override string HelpSectionDescription =>
        $"Accepts: {{name}} | {{rg}}/{{name}} | {{sub}}/{{rg}}/{{name}} | portal-URL. "
        + $"{{sub}} can be a GUID, display name, /subscriptions/{{guid}}, or /s/{{guid}}. "
        + $"Combined form overrides --subscription-id and --resource-group (with warning). "
        + $"Prefix with /arm/ in dataplane commands to force ARM lookup.";

    // -----------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the ARM resource described by the option value.
    /// </summary>
    public Task<TResource> ResolveResourceAsync(ArmClient armClient, CancellationToken ct = default)
    {
        var raw = RawResourceValue ?? throw new InvocationException("Resource name is required.");
        return ResolveResourceByRawAsync(raw, armClient, ct);
    }

    /// <summary>
    /// Resolves the ARM resource using an explicit raw value (e.g. with /arm/ prefix stripped).
    /// </summary>
    internal async Task<TResource> ResolveResourceByRawAsync(
        string raw,
        ArmClient armClient,
        CancellationToken ct
    )
    {
        var (subId, rgName, name) = await ResourceNameResolver.ResolveAsync(
            rawValue: raw,
            explicitSubscriptionId: ResourceGroup.Subscription.SubscriptionId,
            explicitResourceGroupName: ResourceGroup.GetWithSource().Value,
            armClient: armClient,
            resourceType: ArmResourceType,
            ct: ct
        );
        return await GetResourceCoreAsync(armClient, subId, rgName, name, ct);
    }

    /// <summary>
    /// Returns completion candidates for the resource name, scoped to the given hints.
    /// </summary>
    public abstract Task<IEnumerable<string>> GetCompletionCandidatesAsync(
        ArmClient armClient,
        string? subscriptionHint,
        string? resourceGroupHint,
        string namePrefix,
        CancellationToken ct = default
    );

    /// <summary>
    /// Template method: fetch the specific ARM resource using already-resolved (non-null) identifiers.
    /// </summary>
    protected abstract Task<TResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSubscriptionId,
        string resolvedResourceGroupName,
        string resourceName,
        CancellationToken ct
    );

    /// <summary>
    /// Resolves a subscription using <see cref="SubscriptionOptionPack.ResolveAsync"/>.
    /// Pass <c>null</c> to fall through to the default subscription.
    /// </summary>
    protected Task<SubscriptionResource> ResolveSubscriptionAsync(
        ArmClient armClient,
        string? hint
    ) => SubscriptionOptionPack.ResolveAsync(armClient, hint);
}

// ---------------------------------------------------------------------------
// Generic completion provider — one per concrete ArmResourceOptionPack subclass
// ---------------------------------------------------------------------------

/// <summary>
/// Completion provider for any <see cref="ArmResourceOptionPack{TResource}"/> subclass.
/// Parses the partial combined-format value that has been typed so far and scopes the
/// name-prefix search accordingly.
/// </summary>
internal sealed class ArmResourceCompletionProvider<TPack, TResource> : ICliCompletionProvider
    where TPack : ArmResourceOptionPack<TResource>, new()
{
    public async ValueTask<IEnumerable<string>> GetCompletionsAsync(CliCompletionContext context)
    {
        var auth = context.GetOptionPack<AuthOptionPack>();
        var credential = auth?.GetCredential() ?? new DefaultAzureCredential();
        var armClient = new ArmClient(credential);
        var word = context.WordToComplete;

        string? subHint = null;
        string? rgHint = null;
        string prefix = word;
        string headPfx = "";

        if (word.Contains('/'))
        {
            var lastSlash = word.LastIndexOf('/');
            var head = word[..lastSlash];
            prefix = word[(lastSlash + 1)..];
            headPfx = word[..(lastSlash + 1)];

            // Parse "head/placeholder" to extract sub/rg from the already-typed segments.
            try
            {
                var p = ResourceIdentifierParser.Parse(head + "/placeholder");
                subHint = ResourceIdentifierParser.NormalizeSubscriptionSegment(
                    p.SubscriptionSegment
                );
                rgHint = ResourceIdentifierParser.NormalizeResourceGroupSegment(
                    p.ResourceGroupSegment
                );
            }
            catch
            {
                // Unparseable partial — fall through to pack-level hints.
            }
        }

        // Fall back to already-specified option packs (no network call — use raw string values).
        subHint ??= context.GetOptionPack<SubscriptionOptionPack>()?.SubscriptionId;
        rgHint ??= context.GetOptionPack<ResourceGroupOptionPack>()?.ResourceGroupName;

        try
        {
            var pack = new TPack();
            var candidates = await pack.GetCompletionCandidatesAsync(
                armClient,
                subHint,
                rgHint,
                prefix
            );
            return candidates.Select(c => headPfx + c);
        }
        catch
        {
            return [];
        }
    }
}
