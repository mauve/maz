using Console.Cli.Commands.Bootstrap;
using Console.Cli.Commands.Iam;
using Console.Cli.Http;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Pim;

/// <summary>Manage Privileged Identity Management (PIM) activations.</summary>
/// <remarks>
/// Activate or deactivate eligible Azure RBAC roles and Entra ID group memberships
/// via Privileged Identity Management (PIM).
/// Running without a subcommand opens an interactive TUI to browse and act on assignments.
///
/// Examples:
///   maz pim
///   maz pim activate Reader
///   maz pim deactivate "Storage Blob"
/// </remarks>
public partial class PimCommandDef(AuthOptionPack auth, InteractiveOptionPack interactive)
    : CommandDef
{
    public override string Name => "pim";
    protected internal override bool IsManualCommand => true;

    private readonly AuthOptionPack _auth = auth;
    private readonly InteractiveOptionPack _interactive = interactive;

    public readonly PimListCommandDef List = new(auth);
    public readonly PimActivateCommandDef Activate = new(auth, interactive);
    public readonly PimDeactivateCommandDef Deactivate = new(auth, interactive);

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!InteractiveOptionPack.IsEffectivelyInteractive(_interactive.Interactive))
            return ShowHelp();

        var log = DiagnosticOptionPack.GetLog();

        var (armCred, pimCred) = _auth.GetPimCredentials(log);
        var principalId =
            await PrincipalResolver.ResolveAsync("me", armCred, log, ct)
            ?? throw new InvocationException("Could not resolve current user identity.");

        var pimClient = new PimClient(armCred, pimCred, log);

        Task<List<PimEligibleAssignment>> eligibleRolesTask;
        Task<List<PimEligibleAssignment>> eligibleDirRolesTask;
        Task<List<PimEligibleAssignment>> eligibleGroupsTask;
        Task<List<PimEligibleAssignment>> activeRolesTask;
        Task<List<PimEligibleAssignment>> activeDirRolesTask;
        Task<List<PimEligibleAssignment>> activeGroupsTask;

        using (var throbber = new Throbber("Fetching PIM assignments..."))
        {
            eligibleRolesTask = pimClient.ListEligibleRolesAsync(principalId, ct);
            eligibleDirRolesTask = pimClient.ListEligibleDirectoryRolesAsync(principalId, ct);
            eligibleGroupsTask = pimClient.ListEligibleGroupsAsync(principalId, ct);
            activeRolesTask = pimClient.ListActiveRolesAsync(principalId, ct);
            activeDirRolesTask = pimClient.ListActiveDirectoryRolesAsync(principalId, ct);
            activeGroupsTask = pimClient.ListActiveGroupsAsync(principalId, ct);

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
            catch { /* individual results checked below */ }
        }

        var eligibleRoles = eligibleRolesTask.IsCompletedSuccessfully ? eligibleRolesTask.Result : [];
        var eligibleDirRoles = eligibleDirRolesTask.IsCompletedSuccessfully ? eligibleDirRolesTask.Result : [];
        var eligibleGroups = eligibleGroupsTask.IsCompletedSuccessfully ? eligibleGroupsTask.Result : [];

        var allEligible = eligibleRoles.Concat(eligibleDirRoles).Concat(eligibleGroups).ToList();

        if (allEligible.Count == 0)
        {
            System.Console.Error.WriteLine("No eligible PIM assignments found.");
            return 0;
        }

        var activeRoles = activeRolesTask.IsCompletedSuccessfully ? activeRolesTask.Result : [];
        var activeDirRoles = activeDirRolesTask.IsCompletedSuccessfully ? activeDirRolesTask.Result : [];
        var activeGroups = activeGroupsTask.IsCompletedSuccessfully ? activeGroupsTask.Result : [];

        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in activeRoles.Concat(activeDirRoles).Concat(activeGroups))
            activeKeys.Add(ActiveKey(a));

        allEligible.Sort(
            (a, b) =>
            {
                var kindCmp = a.Kind.CompareTo(b.Kind);
                return kindCmp != 0
                    ? kindCmp
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            }
        );

        // Pick an assignment
        var listItems = allEligible
            .Select(a =>
            {
                var status = activeKeys.Contains(ActiveKey(a)) ? "Active" : "Eligible";
                return (
                    Label: $"{a.DisplayName} · {status}",
                    Detail: $"[{KindLabel(a.Kind)}] {a.ScopeDisplayName}"
                );
            })
            .ToArray();

        System.Console.Error.WriteLine("Select a PIM assignment:");
        var selectedIdx = RadioList.Show(listItems, 0, multiLine: true, ct);
        var selected = allEligible[selectedIdx];
        var isActive = activeKeys.Contains(ActiveKey(selected));

        // Pick an action
        var actionItems = isActive
            ? new[] { ("Deactivate", ""), ("Cancel", "") }
            : new[] { ("Activate", ""), ("Cancel", "") };

        System.Console.Error.WriteLine("Select action:");
        var actionIdx = RadioList.Show(actionItems, 0, ct);

        if (actionItems[actionIdx].Item1 == "Cancel")
        {
            System.Console.Error.WriteLine("Cancelled.");
            return 0;
        }

        var kindLabel = KindLabel(selected.Kind).ToLowerInvariant();

        if (!isActive)
        {
            // Activate
            System.Console.Error.Write("Justification (optional): ");
            var justification = System.Console.ReadLine()?.Trim() ?? "";
            const string duration = "PT8H";

            using var throbber = new Throbber($"Activating {kindLabel} '{selected.DisplayName}'...");
            try
            {
                switch (selected.Kind)
                {
                    case PimAssignmentKind.Role:
                    {
                        var response = await pimClient.ActivateRoleAsync(selected, justification, duration, ct);
                        if ((int)response.StatusCode >= 400)
                        {
                            var errorBody = await response.Content.ReadAsStringAsync(ct);
                            if (IsAlreadyActiveError(errorBody))
                            {
                                System.Console.Error.WriteLine($"Role '{selected.DisplayName}' is already active.");
                                return 0;
                            }
                            throw new HttpRequestException(
                                $"Activation failed: HTTP {(int)response.StatusCode}\n{errorBody}"
                            );
                        }
                        var armClient = new AzureRestClient(armCred, log);
                        await LroPoller.PollAsync(response, armClient, "2020-10-01", log, ct);
                        break;
                    }
                    case PimAssignmentKind.DirectoryRole:
                        await pimClient.ActivateDirectoryRoleAsync(selected, justification, duration, ct);
                        break;
                    case PimAssignmentKind.Group:
                        await pimClient.ActivateGroupAsync(selected, justification, duration, ct);
                        break;
                }
            }
            catch (HttpRequestException ex) when (IsAlreadyActiveError(ex.Message))
            {
                System.Console.Error.WriteLine($"{KindLabel(selected.Kind)} '{selected.DisplayName}' is already active.");
                return 0;
            }
            System.Console.Error.WriteLine($"Activated {kindLabel} '{selected.DisplayName}' for {duration}.");
        }
        else
        {
            // Deactivate
            using var throbber = new Throbber($"Deactivating {kindLabel} '{selected.DisplayName}'...");
            switch (selected.Kind)
            {
                case PimAssignmentKind.Role:
                {
                    var response = await pimClient.DeactivateRoleAsync(selected, ct);
                    if ((int)response.StatusCode >= 400)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync(ct);
                        throw new HttpRequestException(
                            $"Deactivation failed: HTTP {(int)response.StatusCode}\n{errorBody}"
                        );
                    }
                    var armClient = new AzureRestClient(armCred, log);
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
            System.Console.Error.WriteLine($"Deactivated {kindLabel} '{selected.DisplayName}'.");
        }

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
            PimAssignmentKind.DirectoryRole => "Directory role",
            PimAssignmentKind.Group => "Group",
            _ => kind.ToString(),
        };

    private static bool IsAlreadyActiveError(string message) =>
        message.Contains("RoleAssignmentExists", StringComparison.OrdinalIgnoreCase)
        || message.Contains("ActiveDurationTooShort", StringComparison.OrdinalIgnoreCase)
        || message.Contains("unableToActivateExistingAssignment", StringComparison.OrdinalIgnoreCase)
        || message.Contains("already has an active", StringComparison.OrdinalIgnoreCase);
}
