using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Console.Shared;
using DotMake.CommandLine;

namespace Console.Commands;

[CliCommand(Description = "Manage Azure subscription information.", Parent = typeof(RootCommand))]
public class AccountCommand
{
    // TODO(mauve): Implement renderer.
    [CliCommand(Description = "List all available Azure subscriptions.", Aliases = ["ls"])]
    public class ListCommand
    {
        [CliOption(
            Description = "List all subscriptions from all clouds, including those that are not enabled.",
            Required = false
        )]
        public bool All { get; set; } = false;

        public string? OutputFormat { get; set; }
        public bool OutputIndented { get; set; }

        public required RootCommand Parent { get; set; }

        public async Task RunAsync(CliContext context)
        {
            var armClient = new ArmClient(Parent.Credential);

            await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync())
            {
                if (!All && !subscription.Data.State.Equals(SubscriptionState.Enabled))
                {
                    continue;
                }

                context.Output.WriteLine(
                    $"{subscription.Data.SubscriptionId}: {subscription.Data.DisplayName, 30} ({subscription.Data.State, 15}) {subscription.Data.TenantId}"
                );
            }
        }
    }

    // TODO(mauve): Implement renderer.
    [CliCommand(
        Description = "Show available locations for a subscription.",
        Aliases = ["locations"]
    )]
    public class ListLocationsCommand : ISubscriptionCommand
    {
        public string? SubscriptionId { get; set; }

        public required RootCommand Parent { get; set; }

        public async Task RunAsync(CliContext context)
        {
            var armClient = new ArmClient(Parent.Credential, SubscriptionId);
            var subscription = await this.GetSubscriptionResourceAsync(armClient);

            foreach (var location in subscription.GetLocations())
            {
                context.Output.WriteLine(
                    $"{location.Name, 20}: {location.DisplayName, 30} ({location.LocationType})"
                );
            }
        }
    }
}
