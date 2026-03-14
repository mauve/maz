using System.Text.Json.Nodes;

namespace Console.Cli.Http;

/// <summary>Polls Azure long-running operations until completion.</summary>
public static class LroPoller
{
    /// <summary>
    /// Polls the LRO indicated by the initial response until it succeeds or fails.
    /// Reads <c>Azure-AsyncOperation</c> or <c>Location</c> headers for the polling URL.
    /// </summary>
    public static async Task<JsonNode> PollAsync(
        HttpResponseMessage initial,
        AzureRestClient client,
        string apiVersion,
        CancellationToken ct)
    {
        var pollingUrl = GetPollingUrl(initial);

        if (pollingUrl is null)
        {
            // No LRO headers — treat the initial response body as the result
            initial.EnsureSuccessStatusCode();
            var body = await initial.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(body)
                ? JsonValue.Create((object?)null)!
                : JsonNode.Parse(body)!;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var retryAfter = GetRetryAfter(initial);
            await Task.Delay(TimeSpan.FromSeconds(retryAfter), ct);

            var pollResponse = await client.SendRawAsync(
                HttpMethod.Get,
                pollingUrl,
                apiVersion,
                null,
                ct);

            pollResponse.EnsureSuccessStatusCode();

            var content = await pollResponse.Content.ReadAsStringAsync(ct);
            var node = string.IsNullOrWhiteSpace(content)
                ? null
                : JsonNode.Parse(content);

            var status = node?["status"]?.GetValue<string>()
                ?? node?["properties"]?["provisioningState"]?.GetValue<string>();

            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return node!;

            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                var error = node?["error"]?.ToJsonString() ?? status;
                throw new InvocationException($"LRO operation {status}: {error}");
            }

            // Still in progress — update polling URL if provided in next response
            var nextUrl = GetPollingUrl(pollResponse);
            if (nextUrl is not null)
                pollingUrl = nextUrl;

            initial = pollResponse;
        }
    }

    private static string? GetPollingUrl(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Azure-AsyncOperation", out var asyncOp))
            return asyncOp.FirstOrDefault();

        if (response.Headers.TryGetValues("Location", out var location))
            return location.FirstOrDefault();

        return null;
    }

    private static int GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values)
            && int.TryParse(values.FirstOrDefault(), out var seconds))
            return seconds;

        return 5; // default polling interval
    }
}
