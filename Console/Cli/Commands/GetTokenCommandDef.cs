using Azure.Core;
using Console.Cli.Shared;

namespace Console.Cli.Commands;

/// <summary>Get an access token for Azure resources.</summary>
/// <remarks>
/// This command acquires a token using the configured Azure credential chain.
/// When no --format flag is given the raw token value is printed with no trailing newline,
/// making it easy to use in scripts (e.g. $(maz get-token)).
/// Pass --format text to render a DefinitionList, or any other --format for structured output.
/// </remarks>
public partial class GetTokenCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "get-token";

    /// <summary>The scopes required for the token.</summary>
    [CliOption("--scopes")]
    public partial List<string> Scopes { get; } = ["https://management.azure.com/.default"];

    /// <summary>The parent request ID for the token request.</summary>
    [CliOption("--parent-request-id", Advanced = true)]
    public partial string? ParentRequestId { get; }

    /// <summary>Additional claims to be included in the token.</summary>
    [CliOption("--claims", Advanced = true)]
    public partial string? Claims { get; }

    /// <summary>The tenant ID for the token request.</summary>
    [CliOption("--tenant-id", Advanced = true)]
    public partial string? TenantId { get; }

    /// <summary>Enable Continuous Access Evaluation (CAE).</summary>
    [CliOption("--is-cae-enabled", Advanced = true)]
    public partial bool IsCaeEnabled { get; }

    /// <summary>Enable Proof of Possession (PoP).</summary>
    [CliOption("--is-proof-of-possession-enabled", Advanced = true)]
    public partial bool IsProofOfPossessionEnabled { get; }

    /// <summary>The nonce value required for PoP token requests.</summary>
    [CliOption("--proof-of-possession-nonce", Advanced = true)]
    public partial string? ProofOfPossessionNonce { get; }

    /// <summary>The resource request URI to be authorized with a PoP token.</summary>
    [CliOption("--proof-of-possession-request-uri", Advanced = true)]
    public partial Uri? ProofOfPossessionRequestUri { get; }

    /// <summary>The HTTP method of the resource request (e.g. GET, POST).</summary>
    [CliOption("--proof-of-possession-request-method", Advanced = true)]
    public partial string? ProofOfPossessionRequestMethod { get; }

    public readonly RenderOptionPack Render = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var requestContext = new TokenRequestContext(
            [.. Scopes],
            ParentRequestId,
            Claims,
            TenantId,
            IsCaeEnabled,
            IsProofOfPossessionEnabled,
            ProofOfPossessionNonce,
            ProofOfPossessionRequestUri,
            ProofOfPossessionRequestMethod
        );

        var result = await _auth.GetCredential().GetTokenAsync(requestContext, ct);

        if (Render.Format == null)
        {
            // Default: raw token, no trailing newline — safe for use in scripts
            System.Console.Write(result.Token);
        }
        else
        {
            var renderer = Render.GetRendererFactory().CreateRendererForType<AccessToken>();
            await renderer.RenderAsync(System.Console.Out, result, ct);
        }

        return 0;
    }
}
