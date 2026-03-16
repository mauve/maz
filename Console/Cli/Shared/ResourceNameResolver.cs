using Azure.ResourceManager;
using Azure.ResourceManager.Resources;

namespace Console.Cli.Shared;

/// <summary>
/// Resolves a hierarchical resource name string into (subscriptionId, resourceGroupName, resourceName),
/// mirroring the logic in <see cref="ArmResourceOptionPack{TResource}"/> for generic ARM commands
/// that use <see cref="ResourceGroupOptionPack"/> with a bare name option.
///
/// Accepted input formats (same as option packs):
///   name                              → lookup by name across all RGs in subscription
///   rg/name                           → explicit resource group
///   sub/rg/name                       → explicit subscription + resource group
///   /s/{sub}/rg/name                  → short subscription prefix
///   /subscriptions/{guid}/rg/name     → full subscription prefix
///
/// Combined sub/rg segments conflict with explicit --subscription-id / --resource-group → error.
/// </summary>
public static class ResourceNameResolver
{
    /// <summary>
    /// Resolves the hierarchical resource name and returns the three URL path segments.
    /// When the resource group is not provided (via combined format or --resource-group / env var),
    /// performs an ARM lookup using <paramref name="resourceType"/> to find the resource.
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
            resourceGroup.ResourceGroupName,
            armClient,
            resourceType,
            ct
        );

    /// <summary>
    /// Core resolution logic. Accepts the explicit subscription/resource-group strings directly,
    /// as read from the corresponding option pack properties (null when not supplied by the user).
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
        CancellationToken ct = default
    )
    {
        var parsed = ResourceIdentifierParser.Parse(rawValue);

        bool combinedHasSub = parsed.SubscriptionSegment is not null;
        bool combinedHasRg = parsed.ResourceGroupSegment is not null;

        if (combinedHasSub && explicitSubscriptionId is not null)
            throw new InvocationException(
                "Ambiguous: the combined value contains a subscription segment AND --subscription-id was also specified."
            );
        if (combinedHasRg && explicitResourceGroupName is not null)
            throw new InvocationException(
                "Ambiguous: the combined value contains a resource-group segment AND --resource-group was also specified."
            );

        // Resolve effective subscription string (no ARM call yet)
        var effectiveSub = combinedHasSub
            ? ResourceIdentifierParser.NormalizeSubscriptionSegment(parsed.SubscriptionSegment)
            : explicitSubscriptionId ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");

        // Resolve effective resource group string
        var effectiveRg = combinedHasRg
            ? ResourceIdentifierParser.NormalizeResourceGroupSegment(parsed.ResourceGroupSegment)
            : explicitResourceGroupName
                ?? Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");

        var resourceName = parsed.ResourceNameSegment;

        // Always resolve the subscription hint to a real GUID (handles display names, /s/... etc.)
        var sub = await SubscriptionOptionPack.ResolveAsync(armClient, effectiveSub);

        if (effectiveRg is not null)
            return (sub.Id.Name, effectiveRg, resourceName);

        // RG unknown — ARM lookup: search all RGs in the subscription

        var matches = new List<(string Rg, string Name)>();
        var filter = $"resourceType eq '{resourceType}' and name eq '{resourceName}'";
        await foreach (
            var resource in sub.GetGenericResourcesAsync(filter: filter, cancellationToken: ct)
        )
        {
            var rg = resource.Id?.ResourceGroupName;
            if (rg is not null)
                matches.Add((rg, resource.Data.Name ?? resourceName));
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"Resource '{resourceName}' of type '{resourceType}' not found in subscription '{sub.Id.Name}'."
            ),
            1 => (sub.Id.Name, matches[0].Rg, matches[0].Name),
            _ => throw new InvocationException(
                $"'{resourceName}' is ambiguous — matched {matches.Count} resources:\n"
                    + string.Join(
                        "\n",
                        matches.Select(m => $"  {m.Name}  (resource-group: {m.Rg})")
                    )
            ),
        };
    }
}
