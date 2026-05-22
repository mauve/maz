using System.Text.Json.Nodes;
using Console.Cli.Commands.Iam;
using Console.Cli.Parsing;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Entra;

/// <summary>Show details of a user account.</summary>
/// <remarks>
/// Fetches a user from Microsoft Entra ID via MS Graph v1.0.
/// Additional data such as the manager or assigned licenses can be included
/// with expansion flags; these trigger separate Graph API calls.
///
/// Examples:
///   maz entra user show me
///   maz entra user show alice@contoso.com
///   maz entra user show alice@contoso.com --manager --licenses
///   maz entra user show alice@contoso.com --custom-attributes
///   maz entra user show alice@contoso.com --sign-in-activity --format json
/// </remarks>
public partial class EntraUserShowCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "show";
    protected internal override bool IsManualCommand => true;

    private readonly AuthOptionPack _auth = auth;

    public readonly CliArgument<string> User = new()
    {
        Name = "user",
        Description = "User UPN, object ID, or 'me' for the currently authenticated user.",
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return User;
    }

    /// <summary>Expand manager information (separate Graph API call).</summary>
    [CliOption("--manager")]
    public partial bool Manager { get; }

    /// <summary>Expand assigned license details (separate Graph API call).</summary>
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
        "officeLocation",
        "mobilePhone",
        "businessPhones",
        "accountEnabled",
        "createdDateTime",
        "userType",
    ];

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var userId = GetValue(User);

        if (string.IsNullOrWhiteSpace(userId))
            throw new InvocationException("The <user> argument is required.");

        var cred = _auth.GetCredential(log);

        // Resolve 'me' / UPN to object ID for consistent Graph URL
        var resolvedId = await PrincipalResolver.ResolveAsync(userId, cred, log, ct)
            ?? throw new InvocationException($"Could not resolve user '{userId}'.");

        var selectFields = BuildSelectFields();
        var graphClient = new GraphUserClient(cred, log);

        Task<JsonNode> userTask;
        Task<JsonNode?> managerTask = Task.FromResult<JsonNode?>(null);
        Task<List<JsonNode>> licensesTask = Task.FromResult<List<JsonNode>>([]); 

        using (var throbber = new Throbber($"Fetching user '{userId}'..."))
        {
            userTask = graphClient.GetUserAsync(resolvedId, selectFields, ct);

            if (Manager)
                managerTask = graphClient.GetManagerAsync(resolvedId, ct);

            if (Licenses)
                licensesTask = graphClient.GetLicenseDetailsAsync(resolvedId, ct);

            try
            {
                await Task.WhenAll(userTask, managerTask, licensesTask);
            }
            catch
            { /* individual results checked below */
            }
        }

        var userNode = await userTask;
        var obj = userNode?.AsObject() ?? [];

        if (Manager)
        {
            var manager = managerTask.IsCompletedSuccessfully ? managerTask.Result : null;
            if (manager is JsonObject mgr)
            {
                // Strip OData annotations from manager object before embedding
                mgr.Remove("@odata.type");
                mgr.Remove("@odata.context");
            }
            obj["manager"] = manager is not null ? manager.DeepClone() : null;
        }

        if (Licenses)
        {
            var licenses = licensesTask.IsCompletedSuccessfully ? licensesTask.Result : [];
            var licenseArray = new JsonArray();
            foreach (var lic in licenses)
                licenseArray.Add(lic.DeepClone());
            obj["licenseDetails"] = licenseArray;
        }

        var rendererFactory = Render.GetRendererFactory();
        var renderer = rendererFactory.CreateRendererForType<JsonNode>();
        await renderer.RenderAsync(System.Console.Out, obj, ct);

        return 0;
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
}
