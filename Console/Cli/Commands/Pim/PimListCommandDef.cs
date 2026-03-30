using System.Text.Json.Nodes;
using Console.Cli.Commands.Iam;
using Console.Cli.Parsing;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Pim;

/// <summary>List eligible PIM assignments and their activation status.</summary>
/// <remarks>
/// Shows all eligible Azure RBAC roles and Entra ID group memberships,
/// along with whether each is currently active.
///
/// Examples:
///   maz pim list
///   maz pim list Reader
///   maz pim list --output json
/// </remarks>
public partial class PimListCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "list";
    protected internal override bool IsManualCommand => true;

    private readonly AuthOptionPack _auth = auth;

    public readonly CliArgument<string> Filter = new()
    {
        Name = "filter",
        Description = "Optional name filter (substring match).",
    };

    public readonly RenderOptionPack Render = new();

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return Filter;
    }

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var filterValue = GetValue(Filter);

        // 1. Resolve current user
        var principalId =
            await PrincipalResolver.ResolveAsync("me", cred, log, ct)
            ?? throw new InvocationException("Could not resolve current user identity.");

        // 2. Query eligible and active assignments in parallel
        var pimClient = new PimClient(cred, log);

        var eligibleRolesTask = pimClient.ListEligibleRolesAsync(principalId, ct);
        var eligibleDirRolesTask = pimClient.ListEligibleDirectoryRolesAsync(principalId, ct);
        var eligibleGroupsTask = pimClient.ListEligibleGroupsAsync(principalId, ct);
        var activeRolesTask = pimClient.ListActiveRolesAsync(principalId, ct);
        var activeDirRolesTask = pimClient.ListActiveDirectoryRolesAsync(principalId, ct);
        var activeGroupsTask = pimClient.ListActiveGroupsAsync(principalId, ct);

        using (var throbber = new Throbber("Fetching PIM assignments..."))
        {
            try
            {
                await Task.WhenAll(
                    eligibleRolesTask,
                    eligibleDirRolesTask,
                    eligibleGroupsTask,
                    activeRolesTask,
                    activeDirRolesTask,
                    activeGroupsTask
                );
            }
            catch
            { /* individual results checked below */
            }
        }

        LogIfFaulted(log, "Eligible roles", eligibleRolesTask);
        LogIfFaulted(log, "Eligible directory roles", eligibleDirRolesTask);
        LogIfFaulted(log, "Eligible groups", eligibleGroupsTask);
        LogIfFaulted(log, "Active roles", activeRolesTask);
        LogIfFaulted(log, "Active directory roles", activeDirRolesTask);
        LogIfFaulted(log, "Active groups", activeGroupsTask);

        var eligibleRoles = Result(eligibleRolesTask);
        var eligibleDirRoles = Result(eligibleDirRolesTask);
        var eligibleGroups = Result(eligibleGroupsTask);
        var activeRoles = Result(activeRolesTask);
        var activeDirRoles = Result(activeDirRolesTask);
        var activeGroups = Result(activeGroupsTask);

        if (
            eligibleRoles.Count == 0
            && eligibleDirRoles.Count == 0
            && eligibleGroups.Count == 0
            && eligibleRolesTask.IsFaulted
            && eligibleDirRolesTask.IsFaulted
            && eligibleGroupsTask.IsFaulted
        )
        {
            throw new InvocationException(
                "Failed to query PIM eligible assignments.\n"
                    + $"  Roles: {eligibleRolesTask.Exception?.InnerException?.Message}\n"
                    + $"  Directory roles: {eligibleDirRolesTask.Exception?.InnerException?.Message}\n"
                    + $"  Groups: {eligibleGroupsTask.Exception?.InnerException?.Message}"
            );
        }

        // 3. Build a set of active assignments for status lookup
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in activeRoles.Concat(activeDirRoles).Concat(activeGroups))
            activeKeys.Add(ActiveKey(a));

        // 4. Build output rows from eligible assignments
        var allEligible = eligibleRoles.Concat(eligibleDirRoles).Concat(eligibleGroups).ToList();

        if (!string.IsNullOrWhiteSpace(filterValue))
        {
            allEligible = allEligible
                .Where(a => a.DisplayName.Contains(filterValue, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        allEligible.Sort(
            (a, b) =>
            {
                var kindCmp = a.Kind.CompareTo(b.Kind);
                return kindCmp != 0
                    ? kindCmp
                    : string.Compare(
                        a.DisplayName,
                        b.DisplayName,
                        StringComparison.OrdinalIgnoreCase
                    );
            }
        );

        var structuredOutput =
            Render.Format is OutputFormat.Json or OutputFormat.JsonL or OutputFormat.JsonPretty;

        var output = new JsonArray();
        foreach (var item in allEligible)
        {
            var isActive = activeKeys.Contains(ActiveKey(item));
            output.Add(
                new JsonObject
                {
                    [nameof(Name)] = item.DisplayName,
                    ["Type"] = KindLabel(item.Kind),
                    ["Status"] = isActive ? "Active" : "Eligible",
                    ["Scope"] = structuredOutput ? item.Scope : item.ScopeDisplayName,
                }
            );
        }

        // 5. Render
        var rendererFactory = Render.GetRendererFactory();
        var renderer = rendererFactory.CreateCollectionRenderer<JsonNode>();
        await renderer.RenderAllAsync(System.Console.Out, ToAsyncEnumerable(output), ct);

        return 0;
    }

    private static string ActiveKey(PimEligibleAssignment a) =>
        a.Kind switch
        {
            PimAssignmentKind.Role => $"role:{a.RoleDefinitionId}:{a.Scope}",
            PimAssignmentKind.DirectoryRole => $"dirrole:{a.RoleDefinitionId}",
            PimAssignmentKind.Group => $"group:{a.GroupId}",
            _ => $"{a.Kind}:{a.DisplayName}",
        };

    private static string KindLabel(PimAssignmentKind kind) =>
        kind switch
        {
            PimAssignmentKind.Role => "Role",
            PimAssignmentKind.DirectoryRole => "Directory Role",
            PimAssignmentKind.Group => "Group",
            _ => kind.ToString(),
        };

    private static List<PimEligibleAssignment> Result(Task<List<PimEligibleAssignment>> task) =>
        task.IsCompletedSuccessfully ? task.Result : [];

    private static void LogIfFaulted(DiagnosticLog log, string label, Task task)
    {
        if (task.IsFaulted)
            log.Trace($"{label} query failed: {task.Exception?.InnerException?.Message}");
    }

    private static async IAsyncEnumerable<object> ToAsyncEnumerable(JsonArray items)
    {
        foreach (var item in items)
        {
            if (item is not null)
                yield return item;
        }

        await Task.CompletedTask;
    }
}
