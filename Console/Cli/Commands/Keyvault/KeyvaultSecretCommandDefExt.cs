using Console.Cli.Shared;

namespace Console.Cli.Commands.Generated;

/// <summary>Extension adding the dump command to the keyvault secret group.</summary>
public partial class KeyvaultSecretCommandDef
{
    public readonly KeyvaultSecretDumpCommandDef Dump = new(auth);
}
