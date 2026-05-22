using System.Text.Json.Nodes;
using Console.Cli.Commands.Iam;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Entra;

/// <summary>List user accounts in the tenant.</summary>
/// <remarks>
/// Queries MS Graph v1.0 to enumerate users. Supports OData $filter, keyword
/// search ($search), and convenience shortcuts for common filters.
/// Expansion flags trigger additional Graph calls per user when used with list —
/// this can be slow for large result sets.
///
/// Examples:
///   maz entra user list
///   maz entra user list --department Engineering
///   maz entra user list --search "alice"
///   maz entra user list --filter "startsWith(displayName,'John')"
///   maz entra user list --job-title "Developer" --sign-in-activity --format json
///   maz entra user list --top 50 --extension-attributes
/// </remarks>
public partial class EntraUserListCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "list";
    protected internal override bool IsManualCommand => true;

    private readonly AuthOptionPack _auth = auth;

    // ── Filtering ──────────────────────────────────────────────────────────

    /// <summary>OData $filter expression (e.g. "accountEnabled eq true").</summary>
    [CliOption("--filter")]
    public partial string? Filter { get; }

    /// <summary>Keyword search across displayName, UPN, and mail. Sets ConsistencyLevel: eventual.</summary>
    [CliOption("--search")]
    public partial string? Search { get; }

    /// <summary>Filter by department (convenience shortcut for --filter).</summary>
    [CliOption("--department")]
    public partial string? Department { get; }

    /// <summary>Filter by job title (convenience shortcut for --filter).</summary>
    [CliOption("--job-title")]
    public partial string? JobTitle { get; }

    /// <summary>Maximum number of users to return. Returns all by default.</summary>
    [CliOption("--top")]
    public partial int? Top { get; }

    // ── Expansions ─────────────────────────────────────────────────────────

    /// <summary>Expand manager information for each user (separate Graph API call per user).</summary>
    [CliOption("--manager")]
    public partial bool Manager { get; }

    /// <summary>Expand assigned license details for each user (separate Graph API call per user).</summary>
    [CliOption("--licenses")]
    public partial bool Licenses { get; }

    /// <summary>Include last sign-in activity. Requires Azure AD P2 license.</summary>
    [CliOption("--sign-in-activity")]
    public partial bool SignInActivity { get; }

    /// <summary>Include on-premises extension attributes (extensionAttribute1–15).</summary>
    [CliOption("--extension-attributes")]
    public partial bool ExtensionAttributes { get; }

    /// <summary>Include Azure AD custom security attributes.</summary>
    [CliOption("--custom-security-attributes")]
    public partial bool CustomSecurityAttributes { get; }

    /// <summary>Shortcut for --extension-attributes --custom-security-attributes.</summary>
    [CliOption("--custom-attributes")]
    public partial bool CustomAttributes { get; }

    /// <summary>Comma-separated OData $select fields to append to the default selection.</summary>
    [CliOption("--select")]
    public partial string? Select { get; }

    public readonly RenderOptionPack Render = new();

    private static readonly string[] DefaultSelectFields =
    [
        "id",
        "displayName",
        "userPrincipalName",
        "mail",
        "jobTitle",
        "department",
        "accountEnabled",
        "userType",
    ];

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var graphClient = new GraphUserClient(cred, log);

        var selectFields = BuildSelectFields();
        var filter = BuildFilter();

        var expandManager = Manager;
        var expandLicenses = Licenses;

        var rendererFactory = Render.GetRendererFactory();
        var renderer = rendererFactory.CreateCollectionRenderer<JsonNode>();

        await renderer.RenderAllAsync(
            System.Console.Out,
            FetchUsersAsync(graphClient, selectFields, filter, Search, Top, expandManager, expandLicenses, log, ct),
            ct
        );

        return 0;
    }

    private static async IAsyncEnumerable<JsonNode> FetchUsersAsync(
        GraphUserClient graphClient,
        IEnumerable<string> selectFields,
        string? filter,
        string? search,
        int? top,
        bool expandManager,
        bool expandLicenses,
        DiagnosticLog log,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await foreach (var userNode in graphClient.ListUsersAsync(selectFields, filter, search, top, ct))
        {
            var obj = userNode.AsObject();
            var userId = obj["id"]?.GetValue<string>();

            if (userId is not null && (expandManager || expandLicenses))
            {
                Task<JsonNode?> managerTask = Task.FromResult<JsonNode?>(null);
                Task<List<JsonNode>> licensesTask = Task.FromResult(new List<JsonNode>());

                if (expandManager)
                    managerTask = graphClient.GetManagerAsync(userId, ct);

                if (expandLicenses)
                    licensesTask = graphClient.GetLicenseDetailsAsync(userId, ct);

                try
                {
                    await Task.WhenAll(managerTask, licensesTask);
                }
                catch (Exception ex)
                {
                    log.Trace($"Expansion failed for user {userId}: {ex.Message}");
                }

                if (expandManager)
                {
                    var manager = managerTask.IsCompletedSuccessfully ? managerTask.Result : null;
                    if (manager is JsonObject mgr)
                    {
                        mgr.Remove("@odata.type");
                        mgr.Remove("@odata.context");
                    }
                    obj["manager"] = manager is not null ? manager.DeepClone() : null;
                }

                if (expandLicenses)
                {
                    var licenses = licensesTask.IsCompletedSuccessfully ? licensesTask.Result : [];
                    var licenseArray = new JsonArray();
                    foreach (var lic in licenses)
                        licenseArray.Add(lic.DeepClone());
                    obj["licenseDetails"] = licenseArray;
                }
            }

            yield return obj;
        }
    }

    private List<string> BuildSelectFields()
    {
        var fields = new List<string>(DefaultSelectFields);

        var includeExtension = ExtensionAttributes || CustomAttributes;
        var includeCustomSecurity = CustomSecurityAttributes || CustomAttributes;

        if (includeExtension)
            fields.Add("onPremisesExtensionAttributes");

        if (includeCustomSecurity)
            fields.Add("customSecurityAttributes");

        if (SignInActivity)
            fields.Add("signInActivity");

        if (!string.IsNullOrWhiteSpace(Select))
        {
            foreach (var field in Select!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!fields.Contains(field, StringComparer.OrdinalIgnoreCase))
                    fields.Add(field);
        }

        return fields;
    }

    private string? BuildFilter()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Filter))
            parts.Add(Filter!);

        if (!string.IsNullOrWhiteSpace(Department))
            parts.Add($"department eq '{EscapeODataString(Department!)}'");

        if (!string.IsNullOrWhiteSpace(JobTitle))
            parts.Add($"jobTitle eq '{EscapeODataString(JobTitle!)}'");

        return parts.Count > 0 ? string.Join(" and ", parts) : null;
    }

    private static string EscapeODataString(string value) =>
        value.Replace("'", "''");
}
