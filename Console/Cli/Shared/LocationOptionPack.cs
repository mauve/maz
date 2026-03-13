using Azure.Core;
using System.CommandLine;

namespace Console.Cli.Shared;

public class LocationOptionPack : OptionPack
{
    public readonly Option<AzureLocation> Location;

    public LocationOptionPack()
    {
        Location = new Option<AzureLocation>("--location", ["-l"])
        {
            Description =
                """
            The Azure region for the resource. Allowed values can be found by
            running `maz account list-locations`.
            """,
            Required = true,
            CustomParser = r => new AzureLocation(r.Tokens[0].Value)
        };
    }

    internal override void AddOptionsTo(Command cmd) => cmd.Add(Location);

    public AzureLocation GetLocation() => GetValue(Location);
}
