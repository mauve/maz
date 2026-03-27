using Console.Cli.Shared;

namespace Console.Cli.Commands.Pim;

/// <summary>Manage Privileged Identity Management (PIM) activations.</summary>
/// <remarks>
/// Activate or deactivate eligible Azure RBAC roles and Entra ID group memberships
/// via Privileged Identity Management (PIM).
///
/// Examples:
///   maz pim activate Reader
///   maz pim deactivate "Storage Blob"
/// </remarks>
public partial class PimCommandDef(AuthOptionPack auth, InteractiveOptionPack interactive)
    : CommandDef
{
    public override string Name => "pim";
    protected internal override bool IsManualCommand => true;

    public readonly PimListCommandDef List = new(auth);
    public readonly PimActivateCommandDef Activate = new(auth, interactive);
    public readonly PimDeactivateCommandDef Deactivate = new(auth, interactive);
}
