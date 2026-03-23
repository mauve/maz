using System.Text.Json.Nodes;
using Console.Cli.Http;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Iam;

/// <summary>
/// Resolves a flexible resource identifier to a full ARM scope string.
/// </summary>
internal static class ResourceScopeResolver
{
    /// <summary>
    /// Resolves the user-provided resource string to a full ARM resource ID (scope).
    /// </summary>
    public static async Task<string> ResolveAsync(
        string resource,
        string? resourceType,
        AzureRestClient client,
        DiagnosticLog log,
        CancellationToken ct
    )
    {
        // If it's already a full ARM ID, use it directly
        if (resource.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            && resource.Contains("/providers/", StringComparison.OrdinalIgnoreCase))
        {
            return resource;
        }

        // Parse the flexible input
        var parsed = ResourceIdentifierParser.Parse(resource);

        // Build an ARG query to resolve to a full ARM resource ID
        var kql = $"Resources | where name =~ '{EscapeKql(parsed.ResourceNameSegment)}'";

        if (parsed.ResourceGroupSegment is not null)
            kql += $" | where resourceGroup =~ '{EscapeKql(parsed.ResourceGroupSegment)}'";

        if (resourceType is not null)
            kql += $" | where type =~ '{EscapeKql(resourceType)}'";

        kql += " | project id, subscriptionId, resourceGroup, name, type";

        var body = new JsonObject { ["query"] = kql };

        if (parsed.SubscriptionSegment is not null)
        {
            var subId = ExtractSubscriptionGuid(parsed.SubscriptionSegment);
            if (subId is not null)
                body["subscriptions"] = new JsonArray(JsonValue.Create(subId));
        }

        var response = await client.SendAsync(
            HttpMethod.Post,
            "/providers/Microsoft.ResourceGraph/resources",
            "2024-04-01",
            body,
            ct
        );

        var data = response["data"]?.AsArray();
        if (data is null || data.Count == 0)
            throw new InvocationException(
                $"Resource '{resource}' not found. Verify the name and ensure you have access."
            );

        if (data.Count > 1 && resourceType is null)
        {
            var types = data
                .Select(r => $"  {r?["type"]?.GetValue<string>()} ({r?["id"]?.GetValue<string>()})")
                .ToList();
            throw new InvocationException(
                $"'{resource}' matched {data.Count} resources. Use --resource-type to disambiguate:\n"
                + string.Join("\n", types)
            );
        }

        return data[0]?["id"]?.GetValue<string>()
            ?? throw new InvocationException(
                $"Resource Graph returned a result without an 'id' field for '{resource}'."
            );
    }

    private static string EscapeKql(string value) => value.Replace("'", "\\'");

    private static string? ExtractSubscriptionGuid(string segment)
    {
        if (segment.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = segment.Split('/');
            return parts.Length > 2 ? parts[2] : null;
        }

        if (segment.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            var token = segment[3..];
            var colonIdx = token.IndexOf(':');
            if (colonIdx >= 0 && Guid.TryParse(token[(colonIdx + 1)..], out _))
                return token[(colonIdx + 1)..];
            if (Guid.TryParse(token, out _))
                return token;
            return null;
        }

        if (Guid.TryParse(segment, out var guid))
            return guid.ToString();

        return null;
    }
}
