using Azure.ResourceManager;
using Azure.ResourceManager.Resources;

namespace Console.Cli.Shared;

public partial class ResourceGroupOptionPack : OptionPack
{
    public readonly SubscriptionOptionPack Subscription = new();

    /// <summary>
    /// The name of the resource group.
    /// Defaults to AZURE_RESOURCE_GROUP.
    /// </summary>
    [CliOption("--resource-group", "-g", "--grp")]
    public partial string? ResourceGroupName { get; }

    public override string HelpTitle => "Resource Group";

    public string RequireResourceGroupName()
    {
        var name = ResourceGroupName ?? Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvocationException("--resource-group is required.");
        return name;
    }

    public Task<SubscriptionResource> GetSubscriptionAsync(ArmClient armClient) =>
        Subscription.GetSubscriptionAsync(armClient);
}
