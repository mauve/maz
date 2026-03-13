using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using System.CommandLine;

namespace Console.Cli.Shared;

public class ResourceGroupOptionPack : OptionPack
{
    public readonly SubscriptionOptionPack Subscription = new();

    public readonly Option<string?> ResourceGroupName;

    public ResourceGroupOptionPack()
    {
        ResourceGroupName = new Option<string?>("--resource-group", ["-g", "--grp"])
        {
            Description =
                """
            The name of the resource group.
            Defaults to AZURE_RESOURCE_GROUP.
            """
        };
    }

    internal override void AddOptionsTo(Command cmd)
    {
        Subscription.AddOptionsTo(cmd);
        cmd.Add(ResourceGroupName);
    }

    public string RequireResourceGroupName()
    {
        var name = GetValue(ResourceGroupName) ?? Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvocationException("--resource-group is required.");
        return name;
    }

    public Task<SubscriptionResource> GetSubscriptionAsync(ArmClient armClient) =>
        Subscription.GetSubscriptionAsync(armClient);
}
