using Console.Cli.Commands.Bootstrap;
using Console.Cli.Commands.Iam;
using Console.Cli.Http;
using Console.Cli.Parsing;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Pim;

/// <summary>Activate an eligible PIM role or group membership.</summary>
/// <remarks>
/// Activates a Privileged Identity Management (PIM) eligible assignment.
/// Searches across both Azure RBAC roles and Entra ID group memberships.
///
/// Examples:
///   maz pim activate Reader
///   maz pim activate "Storage Blob" --justification "investigating issue" --duration PT4H
///   maz pim activate "Admin Group"
/// </remarks>
public partial class PimActivateCommandDef(AuthOptionPack auth, InteractiveOptionPack interactive)
    : CommandDef
{
    public override string Name => "activate";
    protected internal override bool IsManualCommand => true;

    private readonly AuthOptionPack _auth = auth;
    private readonly InteractiveOptionPack _interactive = interactive;

    public readonly CliArgument<string> AssignmentName = new()
    {
        Name = "name",
        Description = "Role or group name to activate (substring match).",
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return AssignmentName;
    }

    [CliOption("--justification", "-j")]
    public partial string? Justification { get; }

    [CliOption("--duration", "-d")]
    public partial string? Duration { get; }

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

        var duration = Duration ?? "PT8H";

        // 1. Resolve current user's principal ID
        var principalId =
            await PrincipalResolver.ResolveAsync("me", cred, log, ct)
            ?? throw new InvocationException("Could not resolve current user identity.");

        // 2. Query eligible roles, directory roles, and groups in parallel
        var pimClient = new PimClient(cred, log);
        Task<List<PimEligibleAssignment>> rolesTask;
        Task<List<PimEligibleAssignment>> dirRolesTask;
        Task<List<PimEligibleAssignment>> groupsTask;

        using (var throbber = new Throbber("Fetching eligible assignments..."))
        {
            rolesTask = pimClient.ListEligibleRolesAsync(principalId, ct);
            dirRolesTask = pimClient.ListEligibleDirectoryRolesAsync(principalId, ct);
            groupsTask = pimClient.ListEligibleGroupsAsync(principalId, ct);

            try
            {
                await Task.WhenAll(rolesTask, dirRolesTask, groupsTask);
            }
            catch
            { /* individual results checked below */
            }
        }

        if (rolesTask.IsFaulted)
            log.Trace(
                $"Role eligibility query failed: {rolesTask.Exception?.InnerException?.Message}"
            );
        if (dirRolesTask.IsFaulted)
            log.Trace(
                $"Directory role eligibility query failed: {dirRolesTask.Exception?.InnerException?.Message}"
            );
        if (groupsTask.IsFaulted)
            log.Trace(
                $"Group eligibility query failed: {groupsTask.Exception?.InnerException?.Message}"
            );

        var eligibleRoles = rolesTask.IsCompletedSuccessfully ? rolesTask.Result : [];
        var eligibleDirRoles = dirRolesTask.IsCompletedSuccessfully ? dirRolesTask.Result : [];
        var eligibleGroups = groupsTask.IsCompletedSuccessfully ? groupsTask.Result : [];

        if (
            eligibleRoles.Count == 0
            && eligibleDirRoles.Count == 0
            && eligibleGroups.Count == 0
            && rolesTask.IsFaulted
            && dirRolesTask.IsFaulted
            && groupsTask.IsFaulted
        )
        {
            throw new InvocationException(
                "Failed to query PIM eligible assignments.\n"
                    + $"  Roles: {rolesTask.Exception?.InnerException?.Message}\n"
                    + $"  Directory roles: {dirRolesTask.Exception?.InnerException?.Message}\n"
                    + $"  Groups: {groupsTask.Exception?.InnerException?.Message}"
            );
        }

        var allEligible = eligibleRoles.Concat(eligibleDirRoles).Concat(eligibleGroups).ToList();

        // 3. Filter by name (case-insensitive substring)
        var matches = allEligible
            .Where(a => a.DisplayName.Contains(nameValue, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvocationException(
                $"No eligible PIM assignment found matching '{nameValue}'.\n"
                    + $"Found {allEligible.Count} eligible assignment(s) total."
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
                $"Multiple assignments match '{nameValue}'. Select one:"
            );
            var items = matches
                .Select(m =>
                {
                    return (
                        Label: m.DisplayName,
                        Detail: $"[{KindLabel(m.Kind)}] {m.ScopeDisplayName}"
                    );
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
                $"Multiple PIM assignments match '{nameValue}'. "
                    + $"Use a more specific name or run interactively:\n{listing}"
            );
        }

        // 5. Prompt for justification if interactive and not provided
        var justification = Justification ?? "";
        if (string.IsNullOrWhiteSpace(justification) && isInteractive)
        {
            System.Console.Error.Write("Justification: ");
            justification = System.Console.ReadLine()?.Trim() ?? "";
        }

        // 6. Activate with throbber
        var kindLabel = KindLabel(selected.Kind).ToLowerInvariant();
        using (var throbber = new Throbber($"Activating {kindLabel} '{selected.DisplayName}'..."))
        {
            try
            {
                switch (selected.Kind)
                {
                    case PimAssignmentKind.Role:
                    {
                        var response = await pimClient.ActivateRoleAsync(
                            selected,
                            justification,
                            duration,
                            ct
                        );

                        if ((int)response.StatusCode >= 400)
                        {
                            var errorBody = await response.Content.ReadAsStringAsync(ct);
                            if (IsAlreadyActiveError(errorBody))
                            {
                                System.Console.Error.WriteLine(
                                    $"Role '{selected.DisplayName}' is already active."
                                );
                                return 0;
                            }

                            throw new HttpRequestException(
                                $"Activation failed: HTTP {(int)response.StatusCode}\n{errorBody}"
                            );
                        }

                        var armClient = new AzureRestClient(cred, log);
                        await LroPoller.PollAsync(response, armClient, "2020-10-01", log, ct);
                        break;
                    }

                    case PimAssignmentKind.DirectoryRole:
                        await pimClient.ActivateDirectoryRoleAsync(
                            selected,
                            justification,
                            duration,
                            ct
                        );
                        break;

                    case PimAssignmentKind.Group:
                        await pimClient.ActivateGroupAsync(selected, justification, duration, ct);
                        break;
                }
            }
            catch (HttpRequestException ex) when (IsAlreadyActiveError(ex.Message))
            {
                System.Console.Error.WriteLine(
                    $"{KindLabel(selected.Kind)} '{selected.DisplayName}' is already active."
                );
                return 0;
            }
        }

        System.Console.Error.WriteLine(
            $"Activated {kindLabel} '{selected.DisplayName}' for {duration}."
        );
        return 0;
    }

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
        || message.Contains(
            "unableToActivateExistingAssignment",
            StringComparison.OrdinalIgnoreCase
        )
        || message.Contains("already has an active", StringComparison.OrdinalIgnoreCase);
}
