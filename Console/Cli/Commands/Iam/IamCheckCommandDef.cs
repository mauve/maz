using System.Text.Json.Nodes;
using Azure.Core;
using Console.Cli.Http;
using Console.Cli.Parsing;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Iam;

/// <summary>Check RBAC role assignments for a resource.</summary>
/// <remarks>
/// Shows effective Azure RBAC role assignments at or above a resource scope.
/// Useful for diagnosing 403 errors when using bearer token authentication
/// (e.g. missing Storage Blob Data Reader).
///
/// Examples:
///   maz iam check apidevcur8storage
///   maz iam check apidevcur8storage me
///   maz iam check apidevcur8storage user@contoso.com
///   maz iam check /subscriptions/.../storageAccounts/foo 00000000-0000-0000-0000-000000000000
/// </remarks>
public partial class IamCheckCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "check";
    protected internal override bool IsManualCommand => true;

    private readonly AuthOptionPack _auth = auth;
    private static readonly HttpClient Http = new();

    // ── Positional arguments ─────────────────────────────────────────────

    public readonly CliArgument<string> Resource = new()
    {
        Name = "resource",
        Description =
            "Resource identifier: name, rg/name, sub/rg/name, full ARM ID, or portal URL.",
    };

    public readonly CliArgument<string> Principal = new()
    {
        Name = "principal",
        Description =
            "Principal to check: 'me' (default), user@domain (UPN), or object ID (GUID). "
            + "Omit to show all role assignments.",
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return Resource;
        yield return Principal;
    }

    // ── Options ──────────────────────────────────────────────────────────

    /// <summary>Resource type hint to disambiguate when multiple resources share a name (e.g. Microsoft.Storage/storageAccounts).</summary>
    [CliOption("--resource-type", "-t")]
    public partial string? ResourceType { get; }

    public readonly RenderOptionPack Render = new();
    public readonly SubscriptionOptionPack Subscription = new();

    // ── Execution ────────────────────────────────────────────────────────

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var client = new AzureRestClient(cred, log);

        var resourceValue = GetValue(Resource);
        if (string.IsNullOrWhiteSpace(resourceValue))
            throw new InvocationException("The <resource> argument is required.");

        // 1. Resolve resource → full ARM scope
        var scope = await ResourceScopeResolver.ResolveAsync(
            resourceValue,
            ResourceType,
            client,
            log,
            ct
        );

        // 2. Resolve principal → object ID (or null for "show all")
        var principalValue = Principal.WasProvided ? GetValue(Principal) : "me";
        string? objectId = await PrincipalResolver.ResolveAsync(principalValue, cred, log, ct);

        // 3. Build role assignment filter
        var filter = "atScope()";
        if (objectId is not null)
            filter += $" and assignedTo('{objectId}')";

        var assignmentPath =
            $"{scope}/providers/Microsoft.Authorization/roleAssignments?$filter={Uri.EscapeDataString(filter)}";

        // 4. Fetch role assignments (paginated)
        var assignments = new List<JsonNode>();
        await foreach (
            var item in client.GetAllAsync(assignmentPath, "2022-04-01", "value", "nextLink", ct)
        )
        {
            if (item is JsonNode node)
                assignments.Add(node);
        }

        if (assignments.Count == 0)
        {
            System.Console.Error.WriteLine("No role assignments found.");
            return 0;
        }

        // 5. Batch-resolve role definition IDs → display names
        var roleDefCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
        {
            var roleDefId = assignment["properties"]?["roleDefinitionId"]?.GetValue<string>();
            if (roleDefId is not null && !roleDefCache.ContainsKey(roleDefId))
                roleDefCache[roleDefId] = roleDefId; // placeholder
        }

        // Resolve all unique role definition IDs in parallel
        var resolveTasks = roleDefCache
            .Keys.Select(async id =>
            {
                try
                {
                    var roleDef = await client.SendAsync(
                        HttpMethod.Get,
                        id,
                        "2022-04-01",
                        null,
                        ct
                    );
                    var roleName = roleDef["properties"]?["roleName"]?.GetValue<string>() ?? id;
                    return (Id: id, Name: roleName);
                }
                catch
                {
                    return (Id: id, Name: id);
                }
            })
            .ToList();

        var resolved = await Task.WhenAll(resolveTasks);
        foreach (var (id, name) in resolved)
            roleDefCache[id] = name;

        // 6. Resolve principal IDs and createdBy IDs → display names
        var allIds = assignments
            .SelectMany(a =>
            {
                var props = a["properties"];
                return new[]
                {
                    props?["principalId"]?.GetValue<string>(),
                    props?["createdBy"]?.GetValue<string>(),
                };
            })
            .Where(id => id is not null && Guid.TryParse(id, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var displayNames = await ResolvePrincipalNamesAsync(allIds!, cred, log, ct);

        // 7. Build output objects
        var output = new JsonArray();
        foreach (var assignment in assignments)
        {
            var props = assignment["properties"];
            var roleDefId = props?["roleDefinitionId"]?.GetValue<string>() ?? "";
            var roleName = roleDefCache.GetValueOrDefault(roleDefId, roleDefId);
            var assignmentScope = props?["scope"]?.GetValue<string>() ?? "";
            var principalId = props?["principalId"]?.GetValue<string>() ?? "";
            var principalType = props?["principalType"]?.GetValue<string>() ?? "";
            var principalDisplay = displayNames.GetValueOrDefault(principalId, principalId);
            var createdOn = props?["createdOn"]?.GetValue<string>() ?? "";
            var createdBy = props?["createdBy"]?.GetValue<string>() ?? "";
            var createdByDisplay = displayNames.GetValueOrDefault(createdBy, createdBy);

            output.Add(
                new JsonObject
                {
                    ["Role"] = roleName,
                    ["Scope"] = assignmentScope,
                    ["Principal"] = principalDisplay,
                    ["PrincipalType"] = principalType,
                    ["GrantedOn"] = createdOn,
                    ["GrantedBy"] = createdByDisplay,
                }
            );
        }

        // 8. Render table
        var rendererFactory = Render.GetRendererFactory();
        var renderer = rendererFactory.CreateCollectionRenderer<JsonNode>();
        await renderer.RenderAllAsync(System.Console.Out, ToAsyncEnumerable(output), ct);

        return 0;
    }

    /// <summary>
    /// Resolves principal object IDs to display names via MS Graph directoryObjects/getByIds.
    /// Falls back to the raw ID on failure.
    /// </summary>
    private static async Task<Dictionary<string, string>> ResolvePrincipalNamesAsync(
        List<string> principalIds,
        TokenCredential cred,
        DiagnosticLog log,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (principalIds.Count == 0)
            return result;

        try
        {
            var token = await cred.GetTokenAsync(
                new TokenRequestContext(["https://graph.microsoft.com/.default"]),
                ct
            );

            // getByIds supports up to 1000 IDs per request
            foreach (var batch in Chunk(principalIds, 1000))
            {
                var idsArray = new JsonArray();
                foreach (var id in batch)
                    idsArray.Add(JsonValue.Create(id));

                var body = new JsonObject { ["ids"] = idsArray };
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://graph.microsoft.com/v1.0/directoryObjects/getByIds"
                );
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
                request.Content = new StringContent(
                    body.ToJsonString(),
                    System.Text.Encoding.UTF8,
                    "application/json"
                );

                log.HttpRequest(HttpMethod.Post, request.RequestUri!.ToString(), request);
                var httpResponse = await Http.SendAsync(request, ct);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    log.Trace($"Graph getByIds returned {(int)httpResponse.StatusCode}");
                    break;
                }

                var json = JsonNode.Parse(await httpResponse.Content.ReadAsStringAsync(ct));
                var values = json?["value"]?.AsArray();
                if (values is null)
                    continue;

                foreach (var obj in values)
                {
                    var id = obj?["id"]?.GetValue<string>();
                    if (id is null)
                        continue;

                    var displayName = obj?["displayName"]?.GetValue<string>();
                    if (displayName is not null)
                        result[id] = displayName;
                }
            }
        }
        catch (Exception ex)
        {
            // Graph call failed — fall back to raw IDs, but log for diagnostics
            log.Trace($"Principal name resolution failed: {ex.Message}");
        }

        return result;
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    private static async IAsyncEnumerable<object> ToAsyncEnumerable(JsonArray items)
    {
        foreach (var item in items)
        {
            if (item is not null)
                yield return item;
        }

        await Task.CompletedTask; // ensure async
    }
}
