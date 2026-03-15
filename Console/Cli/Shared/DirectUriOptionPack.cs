using Azure.ResourceManager;

namespace Console.Cli.Shared;

/// <summary>
/// Placeholder option pack for data-plane services that require a direct endpoint URI.
/// These services do not support ARM-based endpoint discovery — users must always supply
/// the endpoint URL via the corresponding <c>--{service}-endpoint</c> CLI flag.
/// </summary>
public class DirectUriOptionPack
{
    /// <summary>
    /// Not supported for direct-URI services. Provide the endpoint URL via the CLI flag instead.
    /// </summary>
#pragma warning disable CA1822
    public Task<Uri> ResolveDataplaneRefAsync(
        ArmClient armClient,
        CancellationToken ct = default
    ) =>
        throw new InvocationException(
            "No ARM resource is associated with this service. "
                + "Please provide the service endpoint URL directly via the endpoint CLI flag."
        );
#pragma warning restore CA1822
}
