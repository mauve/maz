using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Iam;

/// <summary>
/// Resolves a principal identifier (me, UPN, or GUID) to an object ID.
/// </summary>
internal static class PrincipalResolver
{
    /// <summary>
    /// Resolves the principal string to an Azure AD object ID.
    /// Returns null if principal is null (meaning "show all assignments").
    /// </summary>
    public static async Task<string?> ResolveAsync(
        string? principal,
        TokenCredential credential,
        DiagnosticLog log,
        CancellationToken ct
    )
    {
        if (principal is null)
            return null;

        if (principal.Equals("me", StringComparison.OrdinalIgnoreCase))
            return await ResolveMe(credential, ct);

        if (Guid.TryParse(principal, out _))
            return principal;

        // Assume UPN — resolve via MS Graph
        return await ResolveUpnAsync(principal, credential, log, ct);
    }

    private static async Task<string> ResolveMe(TokenCredential credential, CancellationToken ct)
    {
        // Decode the oid claim from the management token (no Graph call needed)
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            ct
        );

        return ExtractOid(token.Token)
            ?? throw new InvocationException(
                "Could not extract object ID (oid) from the access token. "
                + "Ensure you are authenticated with a user or service principal identity."
            );
    }

    private static string? ExtractOid(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) & ~3, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        return JsonNode.Parse(json)?["oid"]?.GetValue<string>();
    }

    private static readonly HttpClient _http = new();

    private static async Task<string> ResolveUpnAsync(
        string upn,
        TokenCredential credential,
        DiagnosticLog log,
        CancellationToken ct
    )
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]),
            ct
        );

        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(upn)}?$select=id";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        log.HttpRequest(HttpMethod.Get, url, request);
        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvocationException(
                $"Failed to resolve user '{upn}' via MS Graph: "
                + $"HTTP {(int)response.StatusCode}\n{errorBody}"
            );
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var json = JsonNode.Parse(content);
        return json?["id"]?.GetValue<string>()
            ?? throw new InvocationException(
                $"MS Graph returned no 'id' for user '{upn}'."
            );
    }
}
