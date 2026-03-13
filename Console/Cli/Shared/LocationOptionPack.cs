using Azure.Core;

namespace Console.Cli.Shared;

public partial class LocationOptionPack : OptionPack
{
    /// <summary>
    /// The Azure region for the resource. Allowed values can be found by
    /// running `maz account list-locations`.
    /// </summary>
    [CliOption("--location", "-l")]
    public partial AzureLocation Location { get; }

    internal override void AddOptionsTo(System.CommandLine.Command cmd)
        => AddGeneratedOptions(cmd);

    public AzureLocation GetLocation() => Location;
}
