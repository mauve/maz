namespace Console.Cli.Commands.Pim;

internal enum PimAssignmentKind
{
    Role,
    DirectoryRole,
    Group,
}

internal sealed record PimEligibleAssignment(
    PimAssignmentKind Kind,
    string DisplayName,
    string Scope,
    string ScopeDisplayName,
    string PrincipalId,
    string RoleDefinitionId,
    string EligibilityScheduleId,
    string GroupId
);
