using System.Text.Json.Nodes;
using Console.Cli.Commands.Iam;
using Console.Cli.Parsing;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Entra;

/// <summary>List the groups a user belongs to.</summary>
/// <remarks>
/// Returns Entra ID group memberships for the specified user.
/// By default only direct memberships are shown. Use --transitive to include
/// all nested group memberships; the output marks each group as Direct or Transitive.
///
/// Examples:
///   maz entra user get-member-groups me
///   maz entra user get-member-groups alice@contoso.com --transitive
///   maz entra user get-member-groups me --security-enabled-only
///   maz entra user get-member-groups me --transitive --security-enabled-only --format json
/// </remarks>
public partial class EntraUserGetMemberGroupsCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "get-member-groups";
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

    /// <summary>
    /// Include transitive group memberships. Groups are labeled as Direct or Transitive.
    /// </summary>
    [CliOption("--transitive")]
    public partial bool Transitive { get; }

    /// <summary>Only return security-enabled groups.</summary>
    [CliOption("--security-enabled-only")]
    public partial bool SecurityEnabledOnly { get; }

    public readonly RenderOptionPack Render = new();

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var userId = GetValue(User);

        if (string.IsNullOrWhiteSpace(userId))
            throw new InvocationException("The <user> argument is required.");

        var cred = _auth.GetCredential(log);

        var resolvedId = await PrincipalResolver.ResolveAsync(userId, cred, log, ct)
            ?? throw new InvocationException($"Could not resolve user '{userId}'.");

        var graphClient = new GraphUserClient(cred, log);

        List<JsonNode> directGroups;
        List<JsonNode> allGroups;

        using (var throbber = new Throbber("Fetching group memberships..."))
        {
            if (Transitive)
            {
                var directTask = graphClient.GetDirectMemberGroupsAsync(resolvedId, ct);
                var transitiveTask = graphClient.GetTransitiveMemberGroupsAsync(resolvedId, ct);

                await Task.WhenAll(directTask, transitiveTask);

                directGroups = directTask.Result;
                allGroups = transitiveTask.Result;
            }
            else
            {
                directGroups = await graphClient.GetDirectMemberGroupsAsync(resolvedId, ct);
                allGroups = directGroups;
            }
        }

        // Build a set of directly-assigned group IDs for quick lookup
        var directGroupIds = new HashSet<string>(
            directGroups
                .Select(g => g["id"]?.GetValue<string>())
                .Where(id => id is not null)
                .Select(id => id!),
            StringComparer.OrdinalIgnoreCase
        );

        var output = new List<JsonObject>();
        foreach (var group in allGroups)
        {
            if (group is not JsonObject g)
                continue;

            // Apply security-enabled filter
            if (SecurityEnabledOnly)
            {
                var secEnabled = g["securityEnabled"]?.GetValue<bool>();
                if (secEnabled is not true)
                    continue;
            }

            var id = g["id"]?.GetValue<string>() ?? "";
            var membership = Transitive && !directGroupIds.Contains(id) ? "Transitive" : "Direct";

            output.Add(new JsonObject
            {
                ["id"] = id,
                ["displayName"] = g["displayName"]?.GetValue<string>(),
                ["mail"] = g["mail"]?.GetValue<string>(),
                ["securityEnabled"] = g["securityEnabled"]?.GetValue<bool>(),
                ["mailEnabled"] = g["mailEnabled"]?.GetValue<bool>(),
                ["groupTypes"] = g["groupTypes"]?.DeepClone(),
                ["membership"] = membership,
            });
        }

        // Sort: Direct first, then Transitive, alphabetically within each group
        output.Sort((a, b) =>
        {
            var ma = a["membership"]?.GetValue<string>() ?? "";
            var mb = b["membership"]?.GetValue<string>() ?? "";
            var cmp = string.Compare(ma, mb, StringComparison.Ordinal);
            if (cmp != 0) return cmp;
            var da = a["displayName"]?.GetValue<string>() ?? "";
            var db = b["displayName"]?.GetValue<string>() ?? "";
            return string.Compare(da, db, StringComparison.OrdinalIgnoreCase);
        });

        var rendererFactory = Render.GetRendererFactory();
        var renderer = rendererFactory.CreateCollectionRenderer<JsonNode>();
        await renderer.RenderAllAsync(System.Console.Out, ToAsyncEnumerable(output, ct), ct);

        return 0;
    }

    private static async IAsyncEnumerable<JsonNode> ToAsyncEnumerable(
        IEnumerable<JsonObject> items,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
        await Task.CompletedTask;
    }
}
