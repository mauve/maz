using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Entra;

/// <summary>
/// Lightweight HTTP facade for MS Graph v1.0 user resources.
/// Uses a single credential acquiring the Graph scope on demand.
/// </summary>
internal sealed class GraphUserClient
{
    private static readonly HttpClient Http = new();
    private readonly TokenCredential _credential;
    private readonly DiagnosticLog _log;

    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    public GraphUserClient(TokenCredential credential, DiagnosticLog log)
    {
        _credential = credential;
        _log = log;
    }

    // ── User ─────────────────────────────────────────────────────────────

    /// <summary>Fetches a single user by id/UPN with the specified $select fields.</summary>
    public async Task<JsonNode> GetUserAsync(
        string userId,
        IEnumerable<string> selectFields,
        CancellationToken ct
    )
    {
        var select = string.Join(",", selectFields);
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(userId)}?$select={select}";
        return await SendAsync(HttpMethod.Get, url, null, ct);
    }

    /// <summary>Fetches the manager of a user. Returns null if no manager is set.</summary>
    public async Task<JsonNode?> GetManagerAsync(string userId, CancellationToken ct)
    {
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(userId)}/manager";
        try
        {
            return await SendAsync(HttpMethod.Get, url, null, ct);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404") || ex.Message.Contains("ResourceNotFound"))
        {
            return null;
        }
    }

    /// <summary>Fetches the license details for a user.</summary>
    public async Task<List<JsonNode>> GetLicenseDetailsAsync(string userId, CancellationToken ct)
    {
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(userId)}/licenseDetails";
        return await GetAllAsync(url, ct);
    }

    // ── User list ─────────────────────────────────────────────────────────

    /// <summary>
    /// Streams all users matching the specified criteria.
    /// When <paramref name="search"/> is non-null the request includes
    /// <c>ConsistencyLevel: eventual</c> as required by Graph.
    /// </summary>
    public async IAsyncEnumerable<JsonNode> ListUsersAsync(
        IEnumerable<string> selectFields,
        string? filter,
        string? search,
        int? top,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var sb = new StringBuilder(GraphBaseUrl).Append("/users?$select=");
        sb.Append(string.Join(",", selectFields));

        if (!string.IsNullOrWhiteSpace(filter))
        {
            sb.Append("&$filter=").Append(Uri.EscapeDataString(filter));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            sb.Append("&$search=").Append(Uri.EscapeDataString($"\"{search}\""));
            if (!sb.ToString().Contains("$count=", StringComparison.OrdinalIgnoreCase))
                sb.Append("&$count=true");
        }

        if (top.HasValue)
            sb.Append("&$top=").Append(top.Value);

        var useSearch = !string.IsNullOrWhiteSpace(search);
        string? currentUrl = sb.ToString();

        while (currentUrl is not null)
        {
            ct.ThrowIfCancellationRequested();
            var json = await SendAsync(HttpMethod.Get, currentUrl, null, ct, useConsistencyLevel: useSearch);
            var values = json["value"]?.AsArray();
            if (values is not null)
            {
                foreach (var item in values)
                {
                    if (item is not null)
                        yield return item;
                }
            }
            currentUrl = json["@odata.nextLink"]?.GetValue<string>();
        }
    }

    // ── Member groups ─────────────────────────────────────────────────────

    /// <summary>Returns direct group memberships for a user.</summary>
    public Task<List<JsonNode>> GetDirectMemberGroupsAsync(string userId, CancellationToken ct)
    {
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(userId)}/memberOf/microsoft.graph.group?$select=id,displayName,securityEnabled,mailEnabled,groupTypes,mail";
        return GetAllAsync(url, ct);
    }

    /// <summary>Returns all (direct + transitive) group memberships for a user.</summary>
    public Task<List<JsonNode>> GetTransitiveMemberGroupsAsync(string userId, CancellationToken ct)
    {
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(userId)}/transitiveMemberOf/microsoft.graph.group?$select=id,displayName,securityEnabled,mailEnabled,groupTypes,mail";
        return GetAllAsync(url, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<List<JsonNode>> GetAllAsync(string url, CancellationToken ct)
    {
        var items = new List<JsonNode>();
        string? currentUrl = url;

        while (currentUrl is not null)
        {
            ct.ThrowIfCancellationRequested();
            var json = await SendAsync(HttpMethod.Get, currentUrl, null, ct);
            var values = json["value"]?.AsArray();
            if (values is not null)
            {
                foreach (var item in values)
                {
                    if (item is not null)
                        items.Add(item);
                }
            }
            currentUrl = json["@odata.nextLink"]?.GetValue<string>();
        }

        return items;
    }

    private async Task<JsonNode> SendAsync(
        HttpMethod method,
        string url,
        JsonNode? body,
        CancellationToken ct,
        bool useConsistencyLevel = false
    )
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        if (useConsistencyLevel)
            request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");

        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            );
        }

        _log.HttpRequest(method, url, request);
        var response = await Http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"MS Graph returned {(int)response.StatusCode}: {errorBody}"
            );
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(content)
            ? JsonValue.Create((object?)null)!
            : JsonNode.Parse(content)!;
    }
}
