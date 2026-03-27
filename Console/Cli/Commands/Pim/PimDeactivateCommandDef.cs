using Console.Cli.Commands.Bootstrap;
using Console.Cli.Commands.Iam;
using Console.Cli.Http;
using Console.Cli.Parsing;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Pim;

/// <summary>Deactivate an active PIM role or group membership.</summary>
/// <remarks>
/// Deactivates a currently active Privileged Identity Management (PIM) assignment.
/// Only shows PIM-activated assignments (not permanent ones).
///
/// Examples:
///   maz pim deactivate Reader
///   maz pim deactivate "Storage Blob"
///   maz pim deactivate "Admin Group"
/// </remarks>
public partial class PimDeactivateCommandDef(
    AuthOptionPack auth,
    InteractiveOptionPack interactive
) : CommandDef
{
    public override string Name => "deactivate";
    protected internal override bool IsManualCommand => true;

    private readonly AuthOptionPack _auth = auth;
    private readonly InteractiveOptionPack _interactive = interactive;

    public readonly CliArgument<string> AssignmentName = new()
    {
        Name = "name",
        Description = "Role or group name to deactivate (substring match).",
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return AssignmentName;
    }

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var isInteractive = InteractiveOptionPack.IsEffectivelyInteractive(
            _interactive.Interactive
        );

        var nameValue = GetValue(AssignmentName);
        if (string.IsNullOrWhiteSpace(nameValue))
            throw new InvocationException("The <name> argument is required.");

        // 1. Resolve current user's principal ID
        var principalId =
            await PrincipalResolver.ResolveAsync("me", cred, log, ct)
            ?? throw new InvocationException("Could not resolve current user identity.");

        // 2. Query active roles, directory roles, and groups in parallel
        var pimClient = new PimClient(cred, log);
        Task<List<PimEligibleAssignment>> rolesTask;
        Task<List<PimEligibleAssignment>> dirRolesTask;
        Task<List<PimEligibleAssignment>> groupsTask;

        using (var throbber = new Throbber("Fetching active assignments..."))
        {
            rolesTask = pimClient.ListActiveRolesAsync(principalId, ct);
            dirRolesTask = pimClient.ListActiveDirectoryRolesAsync(principalId, ct);
            groupsTask = pimClient.ListActiveGroupsAsync(principalId, ct);

            try { await Task.WhenAll(rolesTask, dirRolesTask, groupsTask); }
            catch { /* individual results checked below */ }
        }

        if (rolesTask.IsFaulted)
            log.Trace($"Active role query failed: {rolesTask.Exception?.InnerException?.Message}");
        if (dirRolesTask.IsFaulted)
            log.Trace($"Active directory role query failed: {dirRolesTask.Exception?.InnerException?.Message}");
        if (groupsTask.IsFaulted)
            log.Trace($"Active group query failed: {groupsTask.Exception?.InnerException?.Message}");

        var activeRoles = rolesTask.IsCompletedSuccessfully ? rolesTask.Result : [];
        var activeDirRoles = dirRolesTask.IsCompletedSuccessfully ? dirRolesTask.Result : [];
        var activeGroups = groupsTask.IsCompletedSuccessfully ? groupsTask.Result : [];

        if (activeRoles.Count == 0 && activeDirRoles.Count == 0 && activeGroups.Count == 0
            && rolesTask.IsFaulted && dirRolesTask.IsFaulted && groupsTask.IsFaulted)
        {
            throw new InvocationException(
                "Failed to query PIM active assignments.\n"
                + $"  Roles: {rolesTask.Exception?.InnerException?.Message}\n"
                + $"  Directory roles: {dirRolesTask.Exception?.InnerException?.Message}\n"
                + $"  Groups: {groupsTask.Exception?.InnerException?.Message}"
            );
        }

        var allActive = activeRoles.Concat(activeDirRoles).Concat(activeGroups).ToList();

        // 3. Filter by name
        var matches = allActive
            .Where(a =>
                a.DisplayName.Contains(nameValue, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvocationException(
                $"No active PIM assignment found matching '{nameValue}'.\n"
                + $"Found {allActive.Count} active PIM assignment(s) total."
            );
        }

        // 4. Disambiguate
        PimEligibleAssignment selected;
        if (matches.Count == 1)
        {
            selected = matches[0];
        }
        else if (isInteractive)
        {
            System.Console.Error.WriteLine(
                $"Multiple active assignments match '{nameValue}'. Select one:"
            );
            var items = matches
                .Select(m =>
                {
                    return (Label: m.DisplayName, Detail: $"[{KindLabel(m.Kind)}] {m.ScopeDisplayName}");
                })
                .ToArray();

            var index = RadioList.Show(items, 0, ct);
            selected = matches[index];
        }
        else
        {
            var listing = string.Join(
                "\n",
                matches.Select(m =>
                {
                    return $"  - {m.DisplayName} [{KindLabel(m.Kind)}] ({m.ScopeDisplayName})";
                })
            );
            throw new InvocationException(
                $"Multiple active PIM assignments match '{nameValue}'. "
                + $"Use a more specific name or run interactively:\n{listing}"
            );
        }

        // 5. Deactivate with throbber
        var kindLabel = KindLabel(selected.Kind).ToLowerInvariant();
        using (var throbber = new Throbber($"Deactivating {kindLabel} '{selected.DisplayName}'..."))
        {
            switch (selected.Kind)
            {
                case PimAssignmentKind.Role:
                {
                    var response = await pimClient.DeactivateRoleAsync(selected, ct);

                    if ((int)response.StatusCode >= 400)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync(ct);
                        throw new HttpRequestException(
                            $"Deactivation failed: HTTP {(int)response.StatusCode}\n{errorBody}");
                    }

                    var armClient = new AzureRestClient(cred, log);
                    await LroPoller.PollAsync(response, armClient, "2020-10-01", log, ct);
                    break;
                }

                case PimAssignmentKind.DirectoryRole:
                    await pimClient.DeactivateDirectoryRoleAsync(selected, ct);
                    break;

                case PimAssignmentKind.Group:
                    await pimClient.DeactivateGroupAsync(selected, ct);
                    break;
            }
        }

        System.Console.Error.WriteLine(
            $"Deactivated {kindLabel} '{selected.DisplayName}'."
        );
        return 0;
    }

    private static string KindLabel(PimAssignmentKind kind) => kind switch
    {
        PimAssignmentKind.Role => "Role",
        PimAssignmentKind.DirectoryRole => "Directory role",
        PimAssignmentKind.Group => "Group",
        _ => kind.ToString(),
    };
}
