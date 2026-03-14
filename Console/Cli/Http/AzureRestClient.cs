using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;

namespace Console.Cli.Http;

/// <summary>Lightweight HTTP client for Azure REST API calls.</summary>
public sealed class AzureRestClient
{
    private static readonly HttpClient _http = new();
    private readonly TokenCredential _credential;
    private const string ManagementScope = "https://management.azure.com/.default";
    private const string BaseUrl = "https://management.azure.com";

    /// <summary>Initializes a new <see cref="AzureRestClient"/> with the given credential.</summary>
    public AzureRestClient(TokenCredential credential)
    {
        _credential = credential;
    }

    /// <summary>
    /// Sends an authenticated HTTP request and returns the parsed response body.
    /// </summary>
    public async Task<JsonNode> SendAsync(
        HttpMethod method,
        string path,
        string apiVersion,
        JsonNode? body,
        CancellationToken ct)
    {
        var response = await SendRawAsync(method, path, apiVersion, body, ct);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(content)
            ? JsonValue.Create((object?)null)!
            : JsonNode.Parse(content)!;
    }

    /// <summary>
    /// Sends an authenticated HTTP request and returns the raw <see cref="HttpResponseMessage"/>.
    /// Use this for LRO operations where response headers are needed for polling.
    /// </summary>
    public async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string path,
        string apiVersion,
        JsonNode? body,
        CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext([ManagementScope]),
            ct);

        var url = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? $"{path}{(path.Contains('?') ? '&' : '?')}api-version={apiVersion}"
            : $"{BaseUrl}{path}?api-version={apiVersion}";

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json");
        }

        return await _http.SendAsync(request, ct);
    }
}
