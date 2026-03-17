using Azure.Identity;
using Azure.ResourceManager;
using Console.Config;

namespace Console.Cli.Shared;

/// <summary>
/// Resolves a hierarchical resource name string into (subscriptionId, resourceGroupName, resourceName),
/// implementing the full Case 1/2/3 resolution logic from the ARM Resource Resolution Specification.
///
/// Accepted input formats (same as option packs):
///   name                              → Case 3: lookup by name (needs sub + rg from context)
///   rg/name                           → Case 2: explicit resource group, find subscription
///   sub/rg/name                       → Case 1: explicit subscription + resource group → no ARM call
///   /s/{sub}/rg/name                  → Case 1 or 2 depending on sub presence
///   /subscriptions/{guid}/rg/name     → Case 1: full ARM path → no ARM call
///   portal URL                        → extracted ARM resource ID
///
/// Combined sub/rg segments override explicit --subscription-id / --resource-group (with warning).
/// </summary>
public static class ResourceNameResolver
{
    /// <summary>
    /// Resolves the hierarchical resource name and returns the three URL path segments.
    /// </summary>
    public static Task<(
        string SubscriptionId,
        string ResourceGroupName,
        string ResourceName
    )> ResolveAsync(
        string rawValue,
        ResourceGroupOptionPack resourceGroup,
        ArmClient armClient,
        string resourceType,
        CancellationToken ct = default
    ) =>
        ResolveAsync(
            rawValue,
            resourceGroup.Subscription.SubscriptionId,
            resourceGroup.GetWithSource().Value,
            armClient,
            resourceType,
            argClient: null,
            isDestructive: false,
            warningWriter: null,
            ct: ct
        );

    /// <summary>
    /// Core resolution logic. Accepts the explicit subscription/resource-group strings directly.
    /// Injects an optional <see cref="IArgClient"/> for testing; production uses DefaultAzureCredential.
    /// </summary>
    internal static async Task<(
        string SubscriptionId,
        string ResourceGroupName,
        string ResourceName
    )> ResolveAsync(
        string rawValue,
        string? explicitSubscriptionId,
        string? explicitResourceGroupName,
        ArmClient armClient,
        string resourceType,
        IArgClient? argClient = null,
        bool isDestructive = false,
        TextWriter? warningWriter = null,
        CancellationToken ct = default
    )
    {
        var parsed = ResourceIdentifierParser.Parse(rawValue);

        // GAP-11: child path handling
        if (parsed.DiscardedChildPath is not null)
        {
            if (isDestructive)
                throw new InvocationException(
                    $"The resource identifier includes a child resource path '{parsed.DiscardedChildPath}' "
                        + "which is not supported for destructive operations."
                );
            else
                (warningWriter ?? System.Console.Error).WriteLine(
                    $"Warning: ignoring child resource path '{parsed.DiscardedChildPath}' in the resource identifier."
                );
        }

        bool combinedHasSub = parsed.SubscriptionSegment is not null;
        bool combinedHasRg = parsed.ResourceGroupSegment is not null;

        // GAP-1: conflict resolution — warn and use embedded rather than throwing
        string? effectiveSub;
        if (combinedHasSub && explicitSubscriptionId is not null)
        {
            (warningWriter ?? System.Console.Error).WriteLine(
                "Warning: ignoring --subscription-id; using the subscription embedded in the resource value."
            );
            effectiveSub = ResourceIdentifierParser.NormalizeSubscriptionSegment(
                parsed.SubscriptionSegment
            );
        }
        else if (combinedHasSub)
        {
            effectiveSub = ResourceIdentifierParser.NormalizeSubscriptionSegment(
                parsed.SubscriptionSegment
            );
        }
        else
        {
            effectiveSub = explicitSubscriptionId;
        }

        string? effectiveRg;
        if (combinedHasRg && explicitResourceGroupName is not null)
        {
            (warningWriter ?? System.Console.Error).WriteLine(
                "Warning: ignoring --resource-group; using the resource group embedded in the resource value."
            );
            effectiveRg = ResourceIdentifierParser.NormalizeResourceGroupSegment(
                parsed.ResourceGroupSegment
            );
        }
        else if (combinedHasRg)
        {
            effectiveRg = ResourceIdentifierParser.NormalizeResourceGroupSegment(
                parsed.ResourceGroupSegment
            );
        }
        else
        {
            effectiveRg = explicitResourceGroupName;
        }

        var resourceName = parsed.ResourceNameSegment;

        // Normalise subscription: if it's a /subscriptions/{guid} or /s/... form, extract the GUID.
        if (effectiveSub is not null)
            effectiveSub = await ResolveSubscriptionIdAsync(armClient, effectiveSub, ct);

        // CASE 1: both sub and rg known → no ARM/ARG call (GAP-8)
        if (effectiveSub is not null && effectiveRg is not null)
            return (effectiveSub, effectiveRg, resourceName);

        // Fall back to env var for sub if still missing
        if (effectiveSub is null)
        {
            var envSub = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
            if (envSub is not null)
                effectiveSub = await ResolveSubscriptionIdAsync(armClient, envSub, ct);
        }

        // Fall back to config default for sub if still missing
        if (effectiveSub is null)
        {
            var configSub = MazConfig.Current.DefaultSubscriptionId;
            if (configSub is not null)
                effectiveSub = await ResolveSubscriptionIdAsync(armClient, configSub, ct);
        }

        // Fall back to env var / config for rg if still missing
        if (effectiveRg is null)
        {
            effectiveRg =
                Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP")
                ?? MazConfig.Current.DefaultResourceGroup;
        }

        // CASE 1 (re-check after env/config fallback)
        if (effectiveSub is not null && effectiveRg is not null)
            return (effectiveSub, effectiveRg, resourceName);

        // ARG queries require a valid subscription GUID.
        // If effectiveSub is a display name or unrecognised format, try to resolve it via ARM.
        // If resolution fails (e.g. an email address was stored in AZURE_SUBSCRIPTION_ID), clear it
        // so the ARG query searches across all accessible subscriptions instead.
        if (effectiveSub is not null && !Guid.TryParse(effectiveSub, out _))
        {
            try
            {
                var sub = await SubscriptionOptionPack.ResolveAsync(armClient, effectiveSub);
                effectiveSub = sub.Data.SubscriptionId;
            }
            catch
            {
                effectiveSub = null;
            }
        }

        var argClientResolved = argClient ?? new ArmArgClient(new DefaultAzureCredential());
        var config = MazConfig.Current;

        // CASE 2: rg known, sub unknown → find the subscription
        if (effectiveRg is not null)
        {
            // CFG1: check if exactly one configured subscription contains this RG
            if (config.ResolutionFilter.Count > 0)
            {
                var subCandidates = config
                    .ResolutionFilter.Where(e =>
                        e.ResourceGroups.Count == 0
                        || e.ResourceGroups.Any(rg =>
                            rg.Equals(effectiveRg, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    .Select(e => e.SubscriptionId)
                    .ToList();

                if (subCandidates.Count == 1)
                    return (subCandidates[0], effectiveRg, resourceName);
                if (subCandidates.Count > 1)
                    throw new InvocationException(
                        $"Resource group '{effectiveRg}' is ambiguous — it appears in multiple configured subscriptions:\n"
                            + string.Join("\n", subCandidates.Select(s => $"  {s}"))
                    );
            }

            // ARG query: find which subscription contains this RG + resource
            var kql =
                $"Resources | where type =~ '{resourceType}' and name =~ '{resourceName}' "
                + $"and resourceGroup =~ '{effectiveRg}' | project subscriptionId, resourceGroup, name";

            var argResults = await argClientResolved.QueryAsync(kql, subscriptions: null, ct);

            return argResults.Count switch
            {
                0 => throw new InvocationException(
                    $"Resource '{resourceName}' of type '{resourceType}' not found "
                        + $"in resource group '{effectiveRg}' in any accessible subscription."
                ),
                1 => (
                    argResults[0].SubscriptionId,
                    argResults[0].ResourceGroup,
                    argResults[0].Name
                ),
                _ => throw new InvocationException(
                    $"'{resourceName}' in resource group '{effectiveRg}' is ambiguous — "
                        + $"found in {argResults.Count} subscriptions:\n"
                        + string.Join("\n", argResults.Select(r => $"  {r.SubscriptionId}"))
                ),
            };
        }

        // CASE 3: bare name (rg unknown)
        if (effectiveSub is not null)
        {
            // Sub known, rg unknown: ARG query within sub
            var kql =
                $"Resources | where type =~ '{resourceType}' and name =~ '{resourceName}' "
                + "| project subscriptionId, resourceGroup, name";

            var argResults = await argClientResolved.QueryAsync(
                kql,
                subscriptions: [effectiveSub],
                ct
            );

            return argResults.Count switch
            {
                0 => throw new InvocationException(
                    $"Resource '{resourceName}' of type '{resourceType}' not found in subscription '{effectiveSub}'."
                ),
                1 => (
                    argResults[0].SubscriptionId,
                    argResults[0].ResourceGroup,
                    argResults[0].Name
                ),
                _ => throw new InvocationException(
                    $"'{resourceName}' is ambiguous — matched {argResults.Count} resources:\n"
                        + string.Join(
                            "\n",
                            argResults.Select(r =>
                                $"  {r.Name}  (subscription: {r.SubscriptionId}, resource-group: {r.ResourceGroup})"
                            )
                        )
                ),
            };
        }

        // Neither sub nor rg known: ARG query across all subscriptions (scoped to CFG1 if set)
        {
            IEnumerable<string>? subScope =
                config.ResolutionFilter.Count > 0
                    ? config.ResolutionFilter.Select(e => e.SubscriptionId)
                    : null;

            var kql =
                $"Resources | where type =~ '{resourceType}' and name =~ '{resourceName}' "
                + "| project subscriptionId, resourceGroup, name";

            var argResults = await argClientResolved.QueryAsync(kql, subScope, ct);

            return argResults.Count switch
            {
                0 => throw new InvocationException(
                    $"Resource '{resourceName}' of type '{resourceType}' not found in any accessible subscription."
                ),
                1 => (
                    argResults[0].SubscriptionId,
                    argResults[0].ResourceGroup,
                    argResults[0].Name
                ),
                _ => throw new InvocationException(
                    $"'{resourceName}' is ambiguous — matched {argResults.Count} resources:\n"
                        + string.Join(
                            "\n",
                            argResults.Select(r =>
                                $"  {r.Name}  (subscription: {r.SubscriptionId}, resource-group: {r.ResourceGroup})"
                            )
                        )
                ),
            };
        }
    }

    /// <summary>
    /// Normalises a subscription hint to a bare GUID when possible without an ARM call.
    /// Handles /subscriptions/{guid} and /s/{guid} and /s/name:{guid} formats.
    /// Display names and other plain strings are returned as-is (ARM resolution deferred to caller).
    /// </summary>
    private static Task<string?> ResolveSubscriptionIdAsync(
        ArmClient armClient,
        string hint,
        CancellationToken ct
    )
    {
        // If it's already a GUID, return directly.
        if (Guid.TryParse(hint, out var g))
            return Task.FromResult<string?>(g.ToString());

        // /subscriptions/{guid} → extract guid
        if (hint.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = hint.Split('/');
            return Task.FromResult<string?>(parts.Length > 2 ? parts[2] : hint);
        }

        // /s/name:guid → extract guid; /s/guid → return token; /s/displayName → ARM needed
        if (hint.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            var token = hint[3..];
            var colonIdx = token.IndexOf(':');
            if (colonIdx >= 0)
                return Task.FromResult<string?>(token[(colonIdx + 1)..]);
            if (Guid.TryParse(token, out var tg))
                return Task.FromResult<string?>(tg.ToString());
            // /s/displayName → resolve via ARM (async path)
            return ResolveSubscriptionDisplayNameAsync(armClient, token, ct);
        }

        // Plain display name or unknown format — return as-is without ARM call.
        // Case 1 (full identity) will use this value directly.
        // Cases 2/3 that need a real GUID will handle ARM resolution separately.
        return Task.FromResult<string?>(hint);
    }

    private static async Task<string?> ResolveSubscriptionDisplayNameAsync(
        ArmClient armClient,
        string displayName,
        CancellationToken ct
    )
    {
        var sub = await SubscriptionOptionPack.ResolveAsync(armClient, displayName);
        return sub.Id.Name;
    }
}
