using Console.Cli.Shared;

namespace Console.Cli.Commands.Generated;

public partial class StorageCommandDef
{
    public readonly StorageBrowseCommandDef Browse = new(auth);
    public readonly StorageQueryCommandDef Query = new(auth);
}
