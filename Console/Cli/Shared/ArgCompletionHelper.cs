using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Console.Config;

namespace Console.Cli.Shared;

internal static class ArgCompletionHelper
{
    public static async Task<IEnumerable<string>> QueryCompletionCandidatesAsync(
        ArmClient armClient,
        TokenCredential credential,
        string resourceType,
        string? subscriptionHint,
        string? resourceGroupHint,
        string prefix,
        IArgClient? argClient = null,
        CancellationToken ct = default
    )
    {
        var config = MazConfig.Current;
        var argClientResolved = argClient ?? new ArmArgClient(credential ?? new DefaultAzureCredential(), DiagnosticLog.Null);

        // Determine subscription scope for ARG queries
        IEnumerable<string>? subscriptionScope = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(subscriptionHint))
            {
                try
                {
                    var sub = await SubscriptionOptionPack.ResolveAsync(armClient, subscriptionHint);
                    subscriptionScope = new[] { sub.Data.SubscriptionId };
                }
                catch
                {
                    subscriptionScope = null;
                }
            }
            else if (resourceGroupHint is not null && config.ResolutionFilter.Count > 0)
            {
                var subCandidates = config
                    .ResolutionFilter
                    .Where(e =>
                        e.ResourceGroups.Count == 0
                        || e.ResourceGroups.Any(rg => rg.Equals(resourceGroupHint, StringComparison.OrdinalIgnoreCase))
                    )
                    .Select(e => e.SubscriptionId)
                    .ToList();

                if (subCandidates.Count > 0)
                    subscriptionScope = subCandidates;
            }
            else if (string.IsNullOrWhiteSpace(subscriptionHint) && config.ResolutionFilter.Count > 0)
            {
                subscriptionScope = config.ResolutionFilter.Select(e => e.SubscriptionId).ToList();
            }
        }
        catch
        {
            subscriptionScope = null;
        }

        // Build KQL for name-prefix search (case-insensitive)
        var prefixLower = (prefix ?? "").ToLowerInvariant();
        var pEsc = prefixLower.Replace("'", "''");
        string kql;
        if (resourceGroupHint is not null)
        {
            var rgEsc = resourceGroupHint.Replace("'", "''");
            kql = $"Resources | where type =~ '{resourceType}' and resourceGroup =~ '{rgEsc}' and startswith(tolower(name), '{pEsc}') | project subscriptionId, resourceGroup, name | limit 200";
        }
        else
        {
            kql = $"Resources | where type =~ '{resourceType}' and startswith(tolower(name), '{pEsc}') | project subscriptionId, resourceGroup, name | limit 200";
        }

        var argResults = await argClientResolved.QueryAsync(kql, subscriptionScope, ct);
        if (argResults.Count == 0)
            return Array.Empty<string>();

        // Precompute allow/deny sets
        var allowedSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disallowedSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.AllowedSubscriptions.Count > 0)
        {
            foreach (var h in config.AllowedSubscriptions)
            {
                var id = await NormalizeSubscriptionHintToIdAsync(armClient, h);
                if (id is not null) allowedSubs.Add(id);
            }
        }
        if (config.DisallowedSubscriptions.Count > 0)
        {
            foreach (var h in config.DisallowedSubscriptions)
            {
                var id = await NormalizeSubscriptionHintToIdAsync(armClient, h);
                if (id is not null) disallowedSubs.Add(id);
            }
        }

        var allowedRgs = config.AllowedResourceGroups.Select(NormalizeRg).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disallowedRgs = config.DisallowedResourceGroups.Select(NormalizeRg).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deniedResIds = config.DeniedResourceIds.Select(id => NormalizeResourceId(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disallowedResIds = config.DisallowedResourceIds.Select(id => NormalizeResourceId(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<string>();
        foreach (var r in argResults)
        {
            if (!r.Name.StartsWith(prefix ?? "", StringComparison.OrdinalIgnoreCase))
                continue;

            var subId = r.SubscriptionId;
            var rg = r.ResourceGroup;
            var name = r.Name;

            if (allowedSubs.Count > 0 && !allowedSubs.Contains(subId))
                continue;
            if (disallowedSubs.Contains(subId))
                continue;

            var rgNorm = NormalizeRg(rg);
            if (allowedRgs.Count > 0 && !allowedRgs.Contains(rgNorm))
                continue;
            if (disallowedRgs.Contains(rgNorm))
                continue;

            var resourceId = $"/subscriptions/{subId}/resourceGroups/{rg}/providers/{resourceType}/{name}";
            var resourceIdNorm = NormalizeResourceId(resourceId);
            if (deniedResIds.Contains(resourceIdNorm) || disallowedResIds.Contains(resourceIdNorm))
                continue;

            results.Add(name);
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeRg(string rg) => string.IsNullOrEmpty(rg) ? rg : (rg.StartsWith("/rg/", StringComparison.OrdinalIgnoreCase) ? rg[4..] : rg);

    private static string NormalizeResourceId(string id) => id?.TrimEnd('/').ToLowerInvariant() ?? "";

    private static async Task<string?> NormalizeSubscriptionHintToIdAsync(ArmClient armClient, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return null;

        if (Guid.TryParse(hint, out var g))
            return g.ToString();

        if (hint.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            return hint["/subscriptions/".Length..].Trim('/');

        if (hint.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            var token = hint[3..];
            var colonIdx = token.IndexOf(':');
            if (colonIdx >= 0)
                return token[(colonIdx + 1)..];
            if (Guid.TryParse(token, out var tg))
                return token;
            try
            {
                var sub = await SubscriptionOptionPack.ResolveAsync(armClient, token);
                return sub.Data.SubscriptionId;
            }
            catch { return null; }
        }

        try
        {
            var sub = await SubscriptionOptionPack.ResolveAsync(armClient, hint);
            return sub.Data.SubscriptionId;
        }
        catch { return null; }
    }
}
