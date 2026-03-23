using Console.Cli.Shared;

namespace Console.Cli.Commands.Iam;

/// <summary>Check identity and access management (IAM) role assignments.</summary>
/// <remarks>
/// Inspect Azure RBAC role assignments for resources and principals.
/// Useful for diagnosing 403 errors when using bearer token authentication.
/// </remarks>
public partial class IamCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "iam";
    public override string[] Aliases => ["rbac"];
    protected internal override bool IsManualCommand => true;

    public readonly IamCheckCommandDef Check = new(auth);
}
