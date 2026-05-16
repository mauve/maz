using Azure.Core;
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
///   Any valid ARM resource ID (R4)
///   Azure Portal URL (R6)
///
/// Combined subscription/resource-group segments override the standalone
/// --subscription-id / --resource-group options (a warning is written to stderr
/// and the embedded value takes precedence — no error is raised).
///
/// When the resource group or subscription is not fully specified, Azure Resource
/// Graph is used to locate the resource across accessible subscriptions, with CFG1
/// scoping applied when configured.
/// </summary>
public abstract class ArmResourceOptionPack<TResource> : OptionPack
{
    // -----------------------------------------------------------------------
    // Abstract members — subclass supplies the concrete option-pack fields
    // -----------------------------------------------------------------------

    /// <summary>
    /// The <see cref="SubscriptionOptionPack"/> field declared on the concrete subclass.
    /// </summary>
    protected abstract SubscriptionOptionPack SubscriptionPack { get; }

    /// <summary>
    /// The <see cref="ResourceGroupOptionPack"/> field declared on the concrete subclass.
    /// </summary>
    protected abstract ResourceGroupOptionPack ResourceGroupPack { get; }

    /// <summary>
    /// The raw string value typed by the user (the [CliOption]-decorated property on the subclass).
    /// </summary>
    protected abstract string? RawResourceValue { get; }

    /// <summary>
    /// The ARM resource type string for ARG queries (e.g. "Microsoft.KeyVault/vaults").
    /// Used by <see cref="ResolveResourceAsync"/> when the resource group or subscription
    /// must be discovered via Azure Resource Graph.
    /// </summary>
    protected abstract string ResourceType { get; }

    /// <summary>
    /// The short path prefix recognised for this resource type.
    /// Default is empty (no prefix). Data-plane subclasses override to "/arm/".
    /// </summary>
    public virtual string ResourceShortPathPrefix => "";

    // -----------------------------------------------------------------------
    // Help text
    // -----------------------------------------------------------------------

    public override string HelpSectionDescription =>
        $"Accepts: {{name}} | {{rg}}/{{name}} | {{sub}}/{{rg}}/{{name}}. "
        + $"{{sub}} can be a GUID, display name, /subscriptions/{{guid}}, or /s/{{guid}}. "
        + $"Combined form overrides --subscription-id and --resource-group (with a warning). "
        + $"Note: subscription display names containing '/' are not supported in the combined format.";

    // -----------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the ARM resource described by the option value using the full Case 1/2/3
    /// logic from the ARM Resource Resolution Specification. When the subscription or
    /// resource group is not fully specified, Azure Resource Graph is queried.
    /// </summary>
    /// <param name="armClient">ARM client used to fetch the resolved resource.</param>
    /// <param name="credential">
    /// Credential for ARG queries. When <c>null</c> the ARG client falls back to
    /// <see cref="DefaultAzureCredential"/>; pass the command's credential to avoid
    /// an extra credential-chain evaluation.
    /// </param>
    /// <param name="log">Optional diagnostic log.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TResource> ResolveResourceAsync(
        ArmClient armClient,
        TokenCredential? credential = null,
        DiagnosticLog? log = null,
        CancellationToken ct = default
    )
    {
        var rawValue =
            RawResourceValue ?? throw new InvocationException("Resource name is required.");

        // Strip the resource-type short prefix (e.g. /arm/) for data-plane options.
        var shortPrefix = ResourceShortPathPrefix;
        if (
            !string.IsNullOrEmpty(shortPrefix)
            && rawValue.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase)
        )
        {
            rawValue = rawValue[shortPrefix.Length..];
        }

        var (sub, rg, name) = await ResourceNameResolver.ResolveAsync(
            rawValue,
            ResourceGroupPack,
            armClient,
            ResourceType,
            credential ?? new DefaultAzureCredential(),
            log,
            ct
        );

        return await GetResourceCoreAsync(armClient, sub, rg, name, ct);
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
    /// Template method: fetch the specific ARM resource. Called after full resolution
    /// via the ARM Resource Resolution Specification (Case 1/2/3). Both
    /// <paramref name="resolvedSubscription"/> and <paramref name="resolvedResourceGroup"/>
    /// are guaranteed non-null.
    /// </summary>
    protected abstract Task<TResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSubscription,
        string resolvedResourceGroup,
        string resourceName,
        CancellationToken ct
    );

    /// <summary>
    /// Resolves a subscription using <see cref="SubscriptionOptionPack.ResolveAsync"/>.
    /// Used by completion providers; pass <c>null</c> to fall through to the default subscription.
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
        var credential = auth?.GetCredential(DiagnosticLog.Null) ?? new DefaultAzureCredential();
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
