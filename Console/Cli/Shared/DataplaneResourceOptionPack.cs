using Azure.ResourceManager;

namespace Console.Cli.Shared;

/// <summary>
/// Extends <see cref="ArmResourceOptionPack{TResource}"/> with dataplane resolution:
/// after fetching the ARM resource the subclass extracts a dataplane reference
/// (e.g. a vault URI) via <see cref="GetDataplaneRef"/>.
/// </summary>
public abstract class DataplaneResourceOptionPack<TResource, TRef> : ArmResourceOptionPack<TResource>
{
    /// <summary>Extracts the dataplane reference from the resolved ARM resource.</summary>
    protected abstract TRef GetDataplaneRef(TResource resource);

    /// <summary>
    /// Resolves the ARM resource and returns its dataplane reference.
    /// </summary>
    public async Task<TRef> ResolveDataplaneRefAsync(ArmClient armClient, CancellationToken ct = default)
    {
        var resource = await ResolveResourceAsync(armClient, ct);
        return GetDataplaneRef(resource);
    }
}
