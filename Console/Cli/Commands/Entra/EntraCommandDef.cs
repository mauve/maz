using Console.Cli.Shared;

namespace Console.Cli.Commands.Entra;

/// <summary>Manage Microsoft Entra ID (Azure Active Directory) resources.</summary>
/// <remarks>
/// Commands for interacting with Microsoft Entra ID via MS Graph.
/// The 'ad' alias is provided for compatibility with Azure CLI conventions.
///
/// Examples:
///   maz entra user show me
///   maz entra user list --department Engineering
///   maz ad user get-member-groups me
/// </remarks>
public partial class EntraCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "entra";
    public override string[] Aliases => ["ad"];
    protected internal override bool IsManualCommand => true;

    public readonly EntraUserCommandDef User = new(auth);
}
