using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Console.Cli.Auth;
using Console.Cli.Http;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Pim;

/// <summary>
/// Facade for Azure PIM APIs: ARM role-based PIM and MS Graph group-based PIM.
/// Uses the ARM credential for Azure RBAC role PIM, and a separate credential
/// chain with the Microsoft Graph PowerShell client ID for directory role and
/// group PIM (the Azure CLI app registration lacks PIM Graph scopes).
/// </summary>
internal sealed class PimClient
{
    private readonly AzureRestClient _arm;
    private readonly TokenCredential _credential;
    private readonly TokenCredential _graphPimCredential;
    private readonly DiagnosticLog _log;
    private static readonly HttpClient Http = new();

    private const string ArmPimApiVersion = "2020-10-01";
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>Microsoft Graph PowerShell / CLI app registration, has PIM scopes preauthorized.</summary>
    private const string GraphPowerShellClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    private static readonly string[] DirectoryRoleScopes =
        ["https://graph.microsoft.com/RoleManagement.ReadWrite.Directory"];
    private static readonly string[] GroupPimScopes =
        ["https://graph.microsoft.com/PrivilegedAccess.ReadWrite.AzureADGroup"];

    public PimClient(TokenCredential credential, DiagnosticLog log)
    {
        _credential = credential;
        _log = log;
        _arm = new AzureRestClient(credential, log);

        // Build a separate credential chain for Graph PIM calls using the
        // Graph PowerShell app registration which has PIM scopes preauthorized.
        var cache = new MsalCache(log);
        var graphOAuth = new OAuth2Client(cache, log, clientId: GraphPowerShellClientId);
        _graphPimCredential = new ChainedTokenCredential(
            new MsalCacheCredential(cache, graphOAuth, log),
            new BrowserCredential(graphOAuth, log)
        );
    }

    // ── Eligible assignments ──────────────────────────────────────────────

    public async Task<List<PimEligibleAssignment>> ListEligibleRolesAsync(
        string principalId,
        CancellationToken ct
    )
    {
        var filter = Uri.EscapeDataString($"assignedTo('{principalId}')");
        var path =
            $"/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?$filter={filter}";

        var items = new List<JsonNode>();
        await foreach (var item in _arm.GetAllAsync(path, ArmPimApiVersion, "value", "nextLink", ct))
        {
            if (item is JsonNode node)
                items.Add(node);
        }

        // Resolve role definition display names
        var roleDefIds = items
            .Select(i => i["properties"]?["roleDefinitionId"]?.GetValue<string>())
            .Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roleNames = await ResolveRoleDefinitionNamesAsync(roleDefIds!, ct);

        var result = new List<PimEligibleAssignment>();
        foreach (var item in items)
        {
            var props = item["properties"];
            var roleDefId = props?["roleDefinitionId"]?.GetValue<string>() ?? "";
            var scope = props?["scope"]?.GetValue<string>() ?? "";
            var scheduleId =
                props?["roleEligibilityScheduleId"]?.GetValue<string>() ?? "";

            result.Add(new PimEligibleAssignment(
                Kind: PimAssignmentKind.Role,
                DisplayName: roleNames.GetValueOrDefault(roleDefId, roleDefId),
                Scope: scope,
                ScopeDisplayName: FormatScope(scope),
                PrincipalId: principalId,
                RoleDefinitionId: roleDefId,
                EligibilityScheduleId: scheduleId,
                GroupId: ""
            ));
        }

        return result;
    }

    public async Task<List<PimEligibleAssignment>> ListEligibleGroupsAsync(
        string principalId,
        CancellationToken ct
    )
    {
        var url =
            $"{GraphBaseUrl}/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances"
            + $"?$filter=principalId eq '{principalId}'";

        var items = await GraphGetAllAsync(url, ct, GroupPimScopes);

        // Resolve group display names
        var groupIds = items
            .Select(i => i["groupId"]?.GetValue<string>())
            .Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groupNames = await ResolveDirectoryObjectNamesAsync(groupIds!, ct);

        var result = new List<PimEligibleAssignment>();
        foreach (var item in items)
        {
            var groupId = item["groupId"]?.GetValue<string>() ?? "";
            var scheduleId =
                item["eligibilityScheduleId"]?.GetValue<string>()
                ?? item["id"]?.GetValue<string>()
                ?? "";

            result.Add(new PimEligibleAssignment(
                Kind: PimAssignmentKind.Group,
                DisplayName: groupNames.GetValueOrDefault(groupId, groupId),
                Scope: groupId,
                ScopeDisplayName: groupNames.GetValueOrDefault(groupId, groupId),
                PrincipalId: principalId,
                RoleDefinitionId: "",
                EligibilityScheduleId: scheduleId,
                GroupId: groupId
            ));
        }

        return result;
    }

    public async Task<List<PimEligibleAssignment>> ListEligibleDirectoryRolesAsync(
        string principalId,
        CancellationToken ct
    )
    {
        var url =
            $"{GraphBaseUrl}/roleManagement/directory/roleEligibilityScheduleInstances"
            + $"?$filter=principalId eq '{principalId}'";

        var items = await GraphGetAllAsync(url, ct, DirectoryRoleScopes);

        // Resolve directory role definition display names
        var roleDefIds = items
            .Select(i => i["roleDefinitionId"]?.GetValue<string>())
            .Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roleNames = await ResolveDirectoryRoleDefinitionNamesAsync(roleDefIds!, ct);

        var result = new List<PimEligibleAssignment>();
        foreach (var item in items)
        {
            var roleDefId = item["roleDefinitionId"]?.GetValue<string>() ?? "";
            var scheduleId =
                item["roleEligibilityScheduleId"]?.GetValue<string>()
                ?? item["id"]?.GetValue<string>()
                ?? "";

            result.Add(new PimEligibleAssignment(
                Kind: PimAssignmentKind.DirectoryRole,
                DisplayName: roleNames.GetValueOrDefault(roleDefId, roleDefId),
                Scope: "/",
                ScopeDisplayName: "Directory",
                PrincipalId: principalId,
                RoleDefinitionId: roleDefId,
                EligibilityScheduleId: scheduleId,
                GroupId: ""
            ));
        }

        return result;
    }

    public async Task<List<PimEligibleAssignment>> ListActiveDirectoryRolesAsync(
        string principalId,
        CancellationToken ct
    )
    {
        var url =
            $"{GraphBaseUrl}/roleManagement/directory/roleAssignmentScheduleInstances"
            + $"?$filter=principalId eq '{principalId}'";

        var items = await GraphGetAllAsync(url, ct, DirectoryRoleScopes);

        var roleDefIds = items
            .Select(i => i["roleDefinitionId"]?.GetValue<string>())
            .Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roleNames = await ResolveDirectoryRoleDefinitionNamesAsync(roleDefIds!, ct);

        var result = new List<PimEligibleAssignment>();
        foreach (var item in items)
        {
            var roleDefId = item["roleDefinitionId"]?.GetValue<string>() ?? "";
            var assignmentType = item["assignmentType"]?.GetValue<string>();

            if (!string.Equals(assignmentType, "Activated", StringComparison.OrdinalIgnoreCase))
                continue;

            var scheduleId =
                item["roleAssignmentScheduleId"]?.GetValue<string>()
                ?? item["id"]?.GetValue<string>()
                ?? "";

            result.Add(new PimEligibleAssignment(
                Kind: PimAssignmentKind.DirectoryRole,
                DisplayName: roleNames.GetValueOrDefault(roleDefId, roleDefId),
                Scope: "/",
                ScopeDisplayName: "Directory",
                PrincipalId: principalId,
                RoleDefinitionId: roleDefId,
                EligibilityScheduleId: scheduleId,
                GroupId: ""
            ));
        }

        return result;
    }

    // ── Active assignments ────────────────────────────────────────────────

    public async Task<List<PimEligibleAssignment>> ListActiveRolesAsync(
        string principalId,
        CancellationToken ct
    )
    {
        var filter = Uri.EscapeDataString($"assignedTo('{principalId}')");
        var path =
            $"/providers/Microsoft.Authorization/roleAssignmentScheduleInstances?$filter={filter}";

        var items = new List<JsonNode>();
        await foreach (var item in _arm.GetAllAsync(path, ArmPimApiVersion, "value", "nextLink", ct))
        {
            if (item is JsonNode node)
            {
                // Only include PIM-activated assignments, not permanent ones
                var assignmentType = node["properties"]?["assignmentType"]?.GetValue<string>();
                if (string.Equals(assignmentType, "Activated", StringComparison.OrdinalIgnoreCase))
                    items.Add(node);
            }
        }

        var roleDefIds = items
            .Select(i => i["properties"]?["roleDefinitionId"]?.GetValue<string>())
            .Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roleNames = await ResolveRoleDefinitionNamesAsync(roleDefIds!, ct);

        var result = new List<PimEligibleAssignment>();
        foreach (var item in items)
        {
            var props = item["properties"];
            var roleDefId = props?["roleDefinitionId"]?.GetValue<string>() ?? "";
            var scope = props?["scope"]?.GetValue<string>() ?? "";
            var scheduleId =
                props?["roleAssignmentScheduleId"]?.GetValue<string>() ?? "";

            result.Add(new PimEligibleAssignment(
                Kind: PimAssignmentKind.Role,
                DisplayName: roleNames.GetValueOrDefault(roleDefId, roleDefId),
                Scope: scope,
                ScopeDisplayName: FormatScope(scope),
                PrincipalId: principalId,
                RoleDefinitionId: roleDefId,
                EligibilityScheduleId: scheduleId,
                GroupId: ""
            ));
        }

        return result;
    }

    public async Task<List<PimEligibleAssignment>> ListActiveGroupsAsync(
        string principalId,
        CancellationToken ct
    )
    {
        var url =
            $"{GraphBaseUrl}/identityGovernance/privilegedAccess/group/assignmentScheduleInstances"
            + $"?$filter=principalId eq '{principalId}'";

        var items = await GraphGetAllAsync(url, ct, GroupPimScopes);

        var groupIds = items
            .Select(i => i["groupId"]?.GetValue<string>())
            .Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groupNames = await ResolveDirectoryObjectNamesAsync(groupIds!, ct);

        var result = new List<PimEligibleAssignment>();
        foreach (var item in items)
        {
            var groupId = item["groupId"]?.GetValue<string>() ?? "";
            var assignmentType = item["assignmentType"]?.GetValue<string>();

            // Only include PIM-activated assignments
            if (!string.Equals(assignmentType, "Activated", StringComparison.OrdinalIgnoreCase))
                continue;

            var scheduleId =
                item["assignmentScheduleId"]?.GetValue<string>()
                ?? item["id"]?.GetValue<string>()
                ?? "";

            result.Add(new PimEligibleAssignment(
                Kind: PimAssignmentKind.Group,
                DisplayName: groupNames.GetValueOrDefault(groupId, groupId),
                Scope: groupId,
                ScopeDisplayName: groupNames.GetValueOrDefault(groupId, groupId),
                PrincipalId: principalId,
                RoleDefinitionId: "",
                EligibilityScheduleId: scheduleId,
                GroupId: groupId
            ));
        }

        return result;
    }

    // ── Activate / Deactivate ─────────────────────────────────────────────

    public async Task<HttpResponseMessage> ActivateRoleAsync(
        PimEligibleAssignment assignment,
        string justification,
        string duration,
        CancellationToken ct
    )
    {
        var requestId = Guid.NewGuid().ToString();
        var path =
            $"{assignment.Scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{requestId}";

        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["principalId"] = assignment.PrincipalId,
                ["roleDefinitionId"] = assignment.RoleDefinitionId,
                ["requestType"] = "SelfActivate",
                ["linkedRoleEligibilityScheduleId"] = assignment.EligibilityScheduleId,
                ["justification"] = justification,
                ["scheduleInfo"] = new JsonObject
                {
                    ["startDateTime"] = DateTime.UtcNow.ToString("o"),
                    ["expiration"] = new JsonObject
                    {
                        ["type"] = "AfterDuration",
                        ["duration"] = duration,
                    },
                },
            },
        };

        return await _arm.SendRawAsync(HttpMethod.Put, path, ArmPimApiVersion, body, ct);
    }

    public async Task<HttpResponseMessage> DeactivateRoleAsync(
        PimEligibleAssignment assignment,
        CancellationToken ct
    )
    {
        var requestId = Guid.NewGuid().ToString();
        var path =
            $"{assignment.Scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{requestId}";

        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["principalId"] = assignment.PrincipalId,
                ["roleDefinitionId"] = assignment.RoleDefinitionId,
                ["requestType"] = "SelfDeactivate",
                ["linkedRoleEligibilityScheduleId"] = assignment.EligibilityScheduleId,
            },
        };

        return await _arm.SendRawAsync(HttpMethod.Put, path, ArmPimApiVersion, body, ct);
    }

    public async Task ActivateGroupAsync(
        PimEligibleAssignment assignment,
        string justification,
        string duration,
        CancellationToken ct
    )
    {
        var body = new JsonObject
        {
            ["principalId"] = assignment.PrincipalId,
            ["accessId"] = "member",
            ["groupId"] = assignment.GroupId,
            ["action"] = "selfActivate",
            ["justification"] = justification,
            ["scheduleInfo"] = new JsonObject
            {
                ["startDateTime"] = DateTime.UtcNow.ToString("o"),
                ["expiration"] = new JsonObject
                {
                    ["type"] = "afterDuration",
                    ["duration"] = duration,
                },
            },
        };

        var url =
            $"{GraphBaseUrl}/identityGovernance/privilegedAccess/group/assignmentScheduleRequests";
        await GraphPostAsync(url, body, ct, GroupPimScopes);
    }

    public async Task DeactivateGroupAsync(
        PimEligibleAssignment assignment,
        CancellationToken ct
    )
    {
        var body = new JsonObject
        {
            ["principalId"] = assignment.PrincipalId,
            ["accessId"] = "member",
            ["groupId"] = assignment.GroupId,
            ["action"] = "selfDeactivate",
        };

        var url =
            $"{GraphBaseUrl}/identityGovernance/privilegedAccess/group/assignmentScheduleRequests";
        await GraphPostAsync(url, body, ct, GroupPimScopes);
    }

    public async Task ActivateDirectoryRoleAsync(
        PimEligibleAssignment assignment,
        string justification,
        string duration,
        CancellationToken ct
    )
    {
        var body = new JsonObject
        {
            ["principalId"] = assignment.PrincipalId,
            ["roleDefinitionId"] = assignment.RoleDefinitionId,
            ["directoryScopeId"] = "/",
            ["action"] = "selfActivate",
            ["justification"] = justification,
            ["scheduleInfo"] = new JsonObject
            {
                ["startDateTime"] = DateTime.UtcNow.ToString("o"),
                ["expiration"] = new JsonObject
                {
                    ["type"] = "afterDuration",
                    ["duration"] = duration,
                },
            },
        };

        var url =
            $"{GraphBaseUrl}/roleManagement/directory/roleAssignmentScheduleRequests";
        await GraphPostAsync(url, body, ct, DirectoryRoleScopes);
    }

    public async Task DeactivateDirectoryRoleAsync(
        PimEligibleAssignment assignment,
        CancellationToken ct
    )
    {
        var body = new JsonObject
        {
            ["principalId"] = assignment.PrincipalId,
            ["roleDefinitionId"] = assignment.RoleDefinitionId,
            ["directoryScopeId"] = "/",
            ["action"] = "selfDeactivate",
        };

        var url =
            $"{GraphBaseUrl}/roleManagement/directory/roleAssignmentScheduleRequests";
        await GraphPostAsync(url, body, ct, DirectoryRoleScopes);
    }

    // ── Graph helpers ─────────────────────────────────────────────────────

    private async Task<List<JsonNode>> GraphGetAllAsync(string url, CancellationToken ct, string[]? scopes = null)
    {
        var items = new List<JsonNode>();
        string? currentUrl = url;

        while (currentUrl is not null)
        {
            ct.ThrowIfCancellationRequested();

            var json = await GraphSendAsync(HttpMethod.Get, currentUrl, null, ct, scopes);
            var values = json?["value"]?.AsArray();
            if (values is not null)
            {
                foreach (var item in values)
                {
                    if (item is not null)
                        items.Add(item);
                }
            }

            currentUrl = json?["@odata.nextLink"]?.GetValue<string>();
        }

        return items;
    }

    private async Task<JsonNode> GraphPostAsync(
        string url,
        JsonNode body,
        CancellationToken ct,
        string[]? scopes = null
    )
    {
        return await GraphSendAsync(HttpMethod.Post, url, body, ct, scopes);
    }

    private async Task<JsonNode> GraphSendAsync(
        HttpMethod method,
        string url,
        JsonNode? body,
        CancellationToken ct,
        string[]? scopes = null
    )
    {
        scopes ??= ["https://graph.microsoft.com/.default"];
        // Use the Graph PowerShell credential for PIM-scoped calls,
        // fall back to the default credential for .default calls (name resolution etc.)
        var cred = scopes[0].Contains("graph.microsoft.com/.default")
            ? _credential
            : _graphPimCredential;
        var token = await cred.GetTokenAsync(
            new TokenRequestContext(scopes),
            ct
        );

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
        var response = await Http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Graph API returned {(int)response.StatusCode}: {errorBody}"
            );
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(content)
            ? JsonValue.Create((object?)null)!
            : JsonNode.Parse(content)!;
    }

    // ── Name resolution ───────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> ResolveRoleDefinitionNamesAsync(
        List<string> roleDefinitionIds,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (roleDefinitionIds.Count == 0)
            return result;

        var tasks = roleDefinitionIds.Select(async id =>
        {
            try
            {
                var roleDef = await _arm.SendAsync(HttpMethod.Get, id, "2022-04-01", null, ct);
                var name = roleDef["properties"]?["roleName"]?.GetValue<string>() ?? id;
                return (Id: id, Name: name);
            }
            catch
            {
                return (Id: id, Name: id);
            }
        });

        foreach (var (id, name) in await Task.WhenAll(tasks))
            result[id] = name;

        return result;
    }

    private async Task<Dictionary<string, string>> ResolveDirectoryObjectNamesAsync(
        List<string> objectIds,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (objectIds.Count == 0)
            return result;

        try
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://graph.microsoft.com/.default"]),
                ct
            );

            var idsArray = new JsonArray();
            foreach (var id in objectIds)
                idsArray.Add(JsonValue.Create(id));

            var body = new JsonObject { ["ids"] = idsArray };
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{GraphBaseUrl}/directoryObjects/getByIds"
            );
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            );

            _log.HttpRequest(HttpMethod.Post, request.RequestUri!.ToString(), request);
            var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return result;

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var values = json?["value"]?.AsArray();
            if (values is null)
                return result;

            foreach (var obj in values)
            {
                var id = obj?["id"]?.GetValue<string>();
                var displayName = obj?["displayName"]?.GetValue<string>();
                if (id is not null && displayName is not null)
                    result[id] = displayName;
            }
        }
        catch (Exception ex)
        {
            _log.Trace($"Directory object name resolution failed: {ex.Message}");
        }

        return result;
    }

    private async Task<Dictionary<string, string>> ResolveDirectoryRoleDefinitionNamesAsync(
        List<string> roleDefinitionIds,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (roleDefinitionIds.Count == 0)
            return result;

        var tasks = roleDefinitionIds.Select(async id =>
        {
            try
            {
                var json = await GraphSendAsync(
                    HttpMethod.Get,
                    $"{GraphBaseUrl}/roleManagement/directory/roleDefinitions/{id}",
                    null,
                    ct,
                    DirectoryRoleScopes
                );
                var name = json?["displayName"]?.GetValue<string>() ?? id;
                return (Id: id, Name: name);
            }
            catch
            {
                return (Id: id, Name: id);
            }
        });

        foreach (var (id, name) in await Task.WhenAll(tasks))
            result[id] = name;

        return result;
    }

    private static string FormatScope(string scope)
    {
        // "/subscriptions/{sub}/resourceGroups/{rg}/..." → abbreviated form
        var parts = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 4 && parts[0] == "subscriptions" && parts[2] == "resourceGroups")
            return $"{parts[3]}/{string.Join("/", parts.Skip(4))}".TrimEnd('/');

        if (parts.Length >= 2 && parts[0] == "subscriptions")
            return $"subscription:{parts[1][..Math.Min(8, parts[1].Length)]}...";

        return scope;
    }
}
