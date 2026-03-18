using Azure.ResourceManager;
using Console.Cli.Http;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Generated;

/// <summary>
/// Shared helper that resolves a workspace reference (name, GUID, or hierarchical ref)
/// to its Log Analytics customerId and ARM path.
/// </summary>
internal static class LoganalyticsWorkspaceResolver
{
    public static async Task<(string customerId, string armPath)> ResolveWorkspaceCustomerIdAsync(
        string workspaceRef,
        ResourceGroupOptionPack resourceGroup,
        AuthOptionPack auth,
        DiagnosticLog log,
        CancellationToken ct
    )
    {
        var credential = auth.GetCredential(log);
        var armClient = new ArmClient(credential);

        var (sub, rg, name) = await ResourceNameResolver.ResolveAsync(
            workspaceRef,
            resourceGroup,
            armClient,
            "Microsoft.OperationalInsights/workspaces",
            ct
        );

        var restClient = new AzureRestClient(credential, log);
        var path =
            $"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{name}";
        var json = await restClient.SendAsync(HttpMethod.Get, path, "2025-07-01", null, ct);
        var customerId = json?["properties"]?["customerId"]?.GetValue<string>();

        if (customerId is null)
            throw new InvocationException(
                $"Could not read customerId for workspace '{name}' in resource group '{rg}'."
            );

        return (customerId, path);
    }
}
