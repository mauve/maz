using Azure.ResourceManager;

namespace Console.Cli.Shared;

/// <summary>
/// Extends <see cref="ArmResourceOptionPack{TResource}"/> with dataplane resolution:
/// after fetching the ARM resource the subclass extracts a dataplane reference
/// (e.g. a vault URI) via <see cref="GetDataplaneRef"/>.
///
/// Resolution priority for <see cref="ResolveDataplaneRefAsync"/>:
///   1. Raw value starts with "/arm/" → strip prefix, resolve via ARM.
///   2. <see cref="TryParseDirectDataplaneRef"/> succeeds → use the direct reference (no ARM call).
///   3. Otherwise → resolve via ARM.
/// </summary>
public abstract class DataplaneResourceOptionPack<TResource, TRef>
    : ArmResourceOptionPack<TResource>
{
    /// <summary>Extracts the dataplane reference from the resolved ARM resource.</summary>
    protected abstract TRef GetDataplaneRef(TResource resource);

    /// <summary>
    /// Attempts to parse the raw value as a direct dataplane reference (GAP-6).
    /// Override in subclasses to handle native dataplane formats (e.g. https:// URIs for Key Vault).
    /// The default implementation always returns false.
    /// </summary>
    protected virtual bool TryParseDirectDataplaneRef(string raw, out TRef? result)
    {
        result = default;
        return false;
    }

    /// <summary>
    /// Resolves the ARM resource and returns its dataplane reference.
    /// </summary>
    public async Task<TRef> ResolveDataplaneRefAsync(
        ArmClient armClient,
        CancellationToken ct = default
    )
    {
        var raw = RawResourceValue ?? throw new InvocationException("Resource name is required.");

        // GAP-15: /arm/ prefix forces ARM resolution (strips the prefix first)
        if (raw.StartsWith("/arm/", StringComparison.OrdinalIgnoreCase))
        {
            var strippedRaw = raw["/arm/".Length..];
            var resource = await ResolveResourceByRawAsync(strippedRaw, armClient, ct);
            return GetDataplaneRef(resource);
        }

        // GAP-6: try direct dataplane ref (e.g. https:// URI for Key Vault)
        if (TryParseDirectDataplaneRef(raw, out var directRef))
            return directRef!;

        // Standard ARM path
        var armResource = await ResolveResourceAsync(armClient, ct);
        return GetDataplaneRef(armResource);
    }
}
