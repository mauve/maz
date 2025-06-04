using DotMake.CommandLine;

namespace Console.Shared;

public interface IResourceGroupCommand : ISubscriptionCommand
{
    [CliOption(
        Description = """
                The name of the resource group.
                
                Defaults to the value of environment variable AZURE_RESOURCE_GROUP.
            """,
        Required = false,
        Aliases = ["-g", "--grp"]
    )]
    public string? ResourceGroupName { get; set; }
}

public static class IResourceGroupCommandExtensions
{
    public static string RequireResourceGroupName(this IResourceGroupCommand self)
    {
        self.ResourceGroupName ??= Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");

        if (string.IsNullOrWhiteSpace(self.ResourceGroupName))
        {
            throw new InvocationException("--resource-group-name is required.");
        }

        return self.ResourceGroupName;
    }
}
