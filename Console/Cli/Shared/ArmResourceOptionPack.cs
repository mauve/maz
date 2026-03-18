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
///
/// Combined subscription/resource-group segments override the standalone
/// --subscription-id / --resource-group options; specifying both forms causes
/// an error.
///
/// Note: subscription display names containing "/" are not supported in the
/// combined format (they would be misinterpreted as path separators).
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
    /// The short path prefix recognised for this resource type.
    /// Used in help text and stripped from the raw value before parsing.
    /// Default is empty (no prefix). Data-plane subclasses override to "/arm/".
    /// </summary>
    public virtual string ResourceShortPathPrefix => "";

    // -----------------------------------------------------------------------
    // Help text
    // -----------------------------------------------------------------------

    public override string HelpSectionDescription =>
        $"Accepts: {{name}} | {{rg}}/{{name}} | {{sub}}/{{rg}}/{{name}}. "
        + $"{{sub}} can be a GUID, display name, /subscriptions/{{guid}}, or /s/{{guid}}. "
        + $"Combined form overrides --subscription-id and --resource-group. "
        + $"Note: subscription display names containing '/' are not supported in the combined format.";

    // -----------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the ARM resource described by the option value.
    /// </summary>
    public Task<TResource> ResolveResourceAsync(ArmClient armClient, CancellationToken ct = default)
    {
        var (sub, rg, name) = ParseAndValidateSegments();
        return GetResourceCoreAsync(armClient, sub, rg, name, ct);
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
    /// Template method: fetch the specific ARM resource. Called with already-resolved
    /// subscription/resource-group hints (strings, not yet resolved to ARM objects).
    /// </summary>
    protected abstract Task<TResource> GetResourceCoreAsync(
        ArmClient armClient,
        string? resolvedSubscription,
        string? resolvedResourceGroup,
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

    // -----------------------------------------------------------------------
    // Parsing
    // -----------------------------------------------------------------------

    private (string? sub, string? rg, string name) ParseAndValidateSegments()
    {
        var rawValue =
            RawResourceValue ?? throw new InvocationException("Resource name is required.");

        // Strip the resource-type short prefix (e.g. /kv/) before parsing.
        var shortPrefix = ResourceShortPathPrefix;
        if (
            !string.IsNullOrEmpty(shortPrefix)
            && rawValue.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase)
        )
        {
            rawValue = rawValue[shortPrefix.Length..];
        }

        var parsed = ResourceIdentifierParser.Parse(rawValue);

        bool combinedHasSub = parsed.SubscriptionSegment is not null;
        bool combinedHasRg = parsed.ResourceGroupSegment is not null;
        bool explicitSub = SubscriptionPack.SubscriptionId is not null;
        bool explicitRg = ResourceGroupPack.ResourceGroupName is not null;

        if (combinedHasSub && explicitSub)
            throw new InvocationException(
                "Ambiguous: the combined value contains a subscription segment AND --subscription-id was also specified."
            );
        if (combinedHasRg && explicitRg)
            throw new InvocationException(
                "Ambiguous: the combined value contains a resource-group segment AND --resource-group was also specified."
            );

        // If the combined form supplied a subscription, normalise it;
        // otherwise fall through to what --subscription-id / env provides (may be null → default).
        var effectiveSub = combinedHasSub
            ? ResourceIdentifierParser.NormalizeSubscriptionSegment(parsed.SubscriptionSegment)
            : SubscriptionPack.SubscriptionId
                ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");

        var effectiveRg = combinedHasRg
            ? ResourceIdentifierParser.NormalizeResourceGroupSegment(parsed.ResourceGroupSegment)
            : ResourceGroupPack.ResourceGroupName
                ?? Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");

        return (effectiveSub, effectiveRg, parsed.ResourceNameSegment);
    }
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
