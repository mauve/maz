using Console.Cli.Shared;

namespace Console.Cli.Commands.Entra;

/// <summary>Manage Entra ID user accounts.</summary>
/// <remarks>
/// Provides commands to inspect and query user accounts in Microsoft Entra ID.
///
/// Examples:
///   maz entra user show me
///   maz entra user show alice@contoso.com --manager --licenses
///   maz entra user list --department Engineering
///   maz entra user get-member-groups me --transitive
/// </remarks>
public partial class EntraUserCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "user";
    protected internal override bool IsManualCommand => true;

    public readonly EntraUserShowCommandDef Show = new(auth);
    public readonly EntraUserListCommandDef List = new(auth);
    public readonly EntraUserGetMemberGroupsCommandDef GetMemberGroups = new(auth);
}
