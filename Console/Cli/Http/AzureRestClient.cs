using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Console.Cli.Shared;

namespace Console.Cli.Http;

/// <summary>Lightweight HTTP client for Azure REST API calls.</summary>
public sealed class AzureRestClient
{
    private static readonly HttpClient _http = new();
    private readonly TokenCredential _credential;
    private readonly string _scope;
    private readonly DiagnosticLog _log;
    private const string ManagementScope = "https://management.azure.com/.default";
    private const string BaseUrl = "https://management.azure.com";

    /// <summary>Initializes a new <see cref="AzureRestClient"/> with the given credential.</summary>
    /// <param name="credential">The credential to use for authentication.</param>
    /// <param name="log">Diagnostic log for runtime diagnostics.</param>
    /// <param name="scope">The OAuth scope to request. Defaults to the ARM management scope.</param>
    public AzureRestClient(
        TokenCredential credential,
        DiagnosticLog log,
        string scope = ManagementScope
    )
    {
        _credential = credential;
        _log = log;
        _scope = scope;
    }

    /// <summary>
    /// Sends an authenticated HTTP request and returns the parsed response body.
    /// </summary>
    public async Task<JsonNode> SendAsync(
        HttpMethod method,
        string path,
        string apiVersion,
        JsonNode? body,
        CancellationToken ct
    )
    {
        var response = await SendRawAsync(method, path, apiVersion, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n{errorBody}"
            );
        }
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
        CancellationToken ct
    )
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext([_scope]), ct);

        string url;
        if (path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // nextLink URLs from ARM already include api-version; don't duplicate it
            url = path.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? path
                : $"{path}{(path.Contains('?') ? '&' : '?')}api-version={apiVersion}";
        }
        else
        {
            url = $"{BaseUrl}{path}?api-version={apiVersion}";
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            );
        }

        _log.HttpRequest(method, url, request);
        var sw = Stopwatch.StartNew();

        var response = await _http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        return response;
    }
}
