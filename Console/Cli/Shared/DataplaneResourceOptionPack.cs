using System.Net.Http;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Console.Cli.Http;

namespace Console.Cli.Shared;

/// <summary>
/// Extends <see cref="ArmResourceOptionPack{TResource}"/> with dataplane resolution:
/// resolves the ARM resource ID without any Azure SDK network call, then fetches the
/// resource properties via a direct ARM REST call and extracts the dataplane reference
/// (e.g. a vault URI) via <see cref="GetDataplaneRefFromJson"/>.
///
/// Accepts three forms for the resource option value:
///   - Direct endpoint URL (e.g. https://myvault.vault.azure.net) — subclass opt-in via TryParseDirectRef
///   - /arm/{name} — maz resolves via ARM and extracts the endpoint
///   - bare {name}, {rg}/{name}, {sub}/{rg}/{name} — same ARM auto-discovery
/// </summary>
public abstract class DataplaneResourceOptionPack<TResource, TRef>
    : ArmResourceOptionPack<TResource>
    where TResource : ArmResource
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

    /// <summary>The ARM management API version used when fetching resource properties.</summary>
    protected abstract string DataplaneArmApiVersion { get; }

    /// <summary>Extracts the dataplane reference from the ARM REST API JSON response.</summary>
    protected abstract TRef GetDataplaneRefFromJson(JsonNode json);

    /// <summary>
    /// Optional direct-ref hook: subclasses override to parse a direct endpoint URL.
    /// Returns null if the raw value is not a direct endpoint (default).
    /// </summary>
    protected virtual TRef? TryParseDirectRef(string raw) => default;

    /// <summary>
    /// Resolves the dataplane reference using a 3-step process:
    /// 1. Try to parse as a direct endpoint URL (subclass opt-in via TryParseDirectRef).
    /// 2. Resolve the ARM resource ID without any SDK network call or deserialization.
    /// 3. Fetch the resource properties via a direct ARM REST call and extract the endpoint.
    /// </summary>
    /// <param name="armClient">ARM client used to resolve the resource ID.</param>
    /// <param name="credential">
    /// Credential for ARG queries and the direct REST call. When <c>null</c> falls back to
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
        // Step 2: resolve resource ID — GetResourceCoreAsync returns a proxy (Id set, Data null)
        var resource = await ResolveResourceAsync(armClient, credential, log, ct);
        // Step 3: fetch resource properties via a direct ARM REST call (no reflection-based deserialization)
        var cred = credential ?? new AuthOptionPack().GetCredential(log ?? DiagnosticLog.Null);
        var restClient = new AzureRestClient(cred, log ?? DiagnosticLog.Null);
        var json = await restClient.SendAsync(
            HttpMethod.Get,
            resource.Id.ToString(),
            DataplaneArmApiVersion,
            null,
            ct
        );
        return GetDataplaneRefFromJson(json);
    }
}
