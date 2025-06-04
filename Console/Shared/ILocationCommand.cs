using Azure.Core;
using DotMake.CommandLine;

namespace Console.Shared;

public interface ILocationCommand
{
    [CliOption(
        Description = """
            The location of the resource group.

            This is the Azure region where the resource group will be created, allowed values can be
            found by calling `maz account list-locations`.
            """,
        Required = true
    )]
    AzureLocation? Location { get; set; }
}

public static class LocationHelpers
{
    public static AzureLocation RequireLocation(this ILocationCommand command)
    {
        if (command.Location is null)
        {
            throw new InvocationException("--location is required.");
        }

        return command.Location.Value;
    }
}
