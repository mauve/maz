using System.Text.Json.Nodes;
using Azure.Core;
using Console.Cli.Http;

namespace Console.Cli.Shared;

/// <summary>A single resource returned by an Azure Resource Graph query.</summary>
public sealed record ArgResource(string SubscriptionId, string ResourceGroup, string Name);

/// <summary>Abstraction for Azure Resource Graph queries, enabling test injection.</summary>
public interface IArgClient
{
    /// <summary>
    /// Executes a KQL query against Azure Resource Graph and returns matching resources.
    /// </summary>
    /// <param name="kql">The KQL query string (should project subscriptionId, resourceGroup, name).</param>
    /// <param name="subscriptions">Optional subscription scope; null = all accessible subscriptions.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ArgResource>> QueryAsync(
        string kql,
        IEnumerable<string>? subscriptions,
        CancellationToken ct
    );
}

/// <summary>
/// Production implementation of <see cref="IArgClient"/> backed by the Azure Resource Graph REST API.
/// </summary>
public sealed class ArmArgClient : IArgClient
{
    private readonly AzureRestClient _rest;
    private const string ArgApiVersion = "2024-04-01";

    public ArmArgClient(TokenCredential credential)
    {
        _rest = new AzureRestClient(credential);
    }

    public async Task<IReadOnlyList<ArgResource>> QueryAsync(
        string kql,
        IEnumerable<string>? subscriptions,
        CancellationToken ct
    )
    {
        var body = new JsonObject { ["query"] = kql };

        if (subscriptions is not null)
        {
            var arr = new JsonArray();
            foreach (var s in subscriptions)
                arr.Add(s);
            if (arr.Count > 0)
                body["subscriptions"] = arr;
        }

        var response = await _rest.SendAsync(
            HttpMethod.Post,
            "/providers/Microsoft.ResourceGraph/resources",
            ArgApiVersion,
            body,
            ct
        );

        var results = new List<ArgResource>();
        var dataArr = response["data"]?.AsArray();
        if (dataArr is null)
            return results;

        foreach (var row in dataArr)
        {
            var subId = row?["subscriptionId"]?.GetValue<string>();
            var rg = row?["resourceGroup"]?.GetValue<string>();
            var name = row?["name"]?.GetValue<string>();
            if (subId is not null && rg is not null && name is not null)
                results.Add(new ArgResource(subId, rg, name));
        }

        return results;
    }
}
