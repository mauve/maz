using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;

namespace Console.Cli.Shared;

/// <summary>
/// Extends <see cref="ArmResourceOptionPack{TResource}"/> with dataplane resolution:
/// after fetching the ARM resource the subclass extracts a dataplane reference
/// (e.g. a vault URI) via <see cref="GetDataplaneRef"/>.
///
/// Accepts three forms for the resource option value:
///   - Direct endpoint URL (e.g. https://myvault.vault.azure.net) — subclass opt-in via TryParseDirectRef
///   - /arm/{name} — maz resolves via ARM and extracts the endpoint
///   - bare {name}, {rg}/{name}, {sub}/{rg}/{name} — same ARM auto-discovery
/// </summary>
public abstract class DataplaneResourceOptionPack<TResource, TRef>
    : ArmResourceOptionPack<TResource>
{
    /// <summary>
    /// Universal ARM prefix for data-plane options.
    /// </summary>
    public override string ResourceShortPathPrefix => "/arm/";

    public override string HelpSectionDescription =>
        $"Accepts: direct URL | /arm/{{name}} | {{name}} | {{rg}}/{{name}} | {{sub}}/{{rg}}/{{name}}. "
        + $"{{sub}} can be a GUID, display name, /subscriptions/{{guid}}, or /s/{{guid}}. "
        + $"Combined form overrides --subscription-id and --resource-group (with a warning). "
        + $"Note: subscription display names containing '/' are not supported in the combined format.";

    /// <summary>Extracts the dataplane reference from the resolved ARM resource.</summary>
    protected abstract TRef GetDataplaneRef(TResource resource);

    /// <summary>
    /// Optional direct-ref hook: subclasses override to parse a direct endpoint URL.
    /// Returns null if the raw value is not a direct endpoint (default).
    /// </summary>
    protected virtual TRef? TryParseDirectRef(string raw) => default;

    /// <summary>
    /// Resolves the dataplane reference using a 3-step process:
    /// 1. Try to parse as a direct endpoint URL (subclass opt-in via TryParseDirectRef).
    /// 2. Strip /arm/ prefix if present, then resolve via ARM (with full ARG-backed resolution).
    /// 3. Bare name / combined form — same ARM auto-discovery.
    /// </summary>
    /// <param name="armClient">ARM client used to resolve and fetch the resource.</param>
    /// <param name="credential">
    /// Credential passed to ARG queries during resolution. When <c>null</c> falls back to
    /// <see cref="DefaultAzureCredential"/>; pass the command's credential to avoid an extra
    /// credential-chain evaluation.
    /// </param>
    /// <param name="log">Optional diagnostic log.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TRef> ResolveDataplaneRefAsync(
        ArmClient armClient,
        TokenCredential? credential = null,
        DiagnosticLog? log = null,
        CancellationToken ct = default
    )
    {
        var raw = RawResourceValue ?? throw new InvocationException("Resource value is required.");
        // Step 1: direct format (e.g. https:// URI)
        var direct = TryParseDirectRef(raw);
        if (direct is not null)
            return direct;
        // Step 2+3: /arm/ prefix (stripped by ResourceShortPathPrefix) or bare name/combined form
        var resource = await ResolveResourceAsync(armClient, credential, log, ct);
        return GetDataplaneRef(resource);
    }
}
