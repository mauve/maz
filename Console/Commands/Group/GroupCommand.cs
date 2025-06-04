using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Console.Shared;
using DotMake.CommandLine;

namespace Console.Commands.Group;

[CliCommand(
    Description = "Manage resource groups.",
    Aliases = ["grp"],
    Parent = typeof(RootCommand)
)]
public class GroupCommand
{
    [CliCommand(Description = "Create a new resource group.")]
    public class CreateCommand
        : ILocationCommand,
            ITagCommand,
            IResourceGroupCommand,
            IBasicRenderCommand
    {
        public required RootCommand Parent { get; set; }

        public AzureLocation? Location { get; set; }

        public List<Tag>? Tags { get; set; }

        [CliOption(
            Description = "The ID of the resource which manages this resource group.",
            Required = false
        )]
        public string? ManagedBy { get; set; }

        public string? ResourceGroupName { get; set; }

        public string? SubscriptionId { get; set; }

        [CliOption(
            Description = "Specify if the command should wait for the operation to complete.",
            Aliases = ["--wait"],
            Required = false
        )]
        public WaitUntil WaitUntil { get; set; } = WaitUntil.Completed;

        public string? OutputFormat { get; set; }

        public bool OutputIndented { get; set; } = false;

        public async Task RunAsync(CliContext context)
        {
            var rendererFactory = this.GetRendererFactory();

            var armClient = new ArmClient(Parent.Credential, SubscriptionId);
            var subscription = await this.GetSubscriptionResourceAsync(armClient);

            var resourceGroupData = new ResourceGroupData(this.RequireLocation())
            {
                ManagedBy = ManagedBy,
            };
            this.AppendTagsTo(resourceGroupData.Tags);

            ArmOperation<ResourceGroupResource> resourceGroup = await subscription
                .GetResourceGroups()
                .CreateOrUpdateAsync(
                    WaitUntil,
                    this.RequireResourceGroupName(),
                    resourceGroupData,
                    context.CancellationToken
                );

            await rendererFactory
                .CreateRendererForType(resourceGroup.Value.GetType())
                .RenderAsync(context.Output, resourceGroup.Value, context.CancellationToken);
        }
    }

    [CliCommand(Description = "List resource groups.")]
    public class ListCommand : ISubscriptionCommand, IBasicRenderCommand
    {
        public required RootCommand Parent { get; set; }

        public string? SubscriptionId { get; set; }

        public string? OutputFormat { get; set; }

        public bool OutputIndented { get; set; } = false;

        public async Task RunAsync(CliContext context)
        {
            var rendererFactory = this.GetRendererFactory();

            var armClient = new ArmClient(Parent.Credential, SubscriptionId);
            var subscription = await this.GetSubscriptionResourceAsync(armClient);

            var renderer = rendererFactory.CreateRendererForType<ResourceGroupResource>();

            await foreach (var resourceGroup in subscription.GetResourceGroups().GetAllAsync())
            {
                await renderer.RenderAsync(
                    context.Output,
                    resourceGroup,
                    context.CancellationToken
                );
            }
        }
    }
}
