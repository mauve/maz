using Azure.Core;
using Console.Cli.Shared;

namespace Console.Cli.Commands;

/// <summary>Get an access token for Azure resources.</summary>
public partial class GetTokenCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "get-token";

    /// <summary>The scopes required for the token.</summary>
    [CliOption("--scopes", "--resource", "--resources")]
    public partial List<string> Scopes { get; } = ["https://management.azure.com/.default"];

    /// <summary>The parent request ID for the token request.</summary>
    [CliOption("--parent-request-id")]
    public partial string? ParentRequestId { get; }

    /// <summary>Additional claims to be included in the token.</summary>
    [CliOption("--claims")]
    public partial string? Claims { get; }

    /// <summary>The tenant ID for the token request.</summary>
    [CliOption("--tenant-id")]
    public partial string? TenantId { get; }

    /// <summary>Enable Continuous Access Evaluation (CAE).</summary>
    [CliOption("--is-cae-enabled")]
    public partial bool IsCaeEnabled { get; }

    /// <summary>Enable Proof of Possession (PoP).</summary>
    [CliOption("--is-proof-of-possession-enabled")]
    public partial bool IsProofOfPossessionEnabled { get; }

    /// <summary>The nonce value required for PoP token requests.</summary>
    [CliOption("--proof-of-possession-nonce")]
    public partial string? ProofOfPossessionNonce { get; }

    /// <summary>The resource request URI to be authorized with a PoP token.</summary>
    [CliOption("--proof-of-possession-request-uri")]
    public partial Uri? ProofOfPossessionRequestUri { get; }

    /// <summary>The HTTP method of the resource request (e.g. GET, POST).</summary>
    [CliOption("--proof-of-possession-request-method")]
    public partial string? ProofOfPossessionRequestMethod { get; }

    /// <summary>Print the raw token.</summary>
    [CliOption("--print-raw-token")]
    public partial bool PrintRawToken { get; } = true;

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

        if (PrintRawToken)
        {
            System.Console.Write(result.Token);
        }
        else
        {
            System.Console.WriteLine($"Access Token: {result.Token}");
            System.Console.WriteLine($"Expires On:   {result.ExpiresOn}");
            System.Console.WriteLine($"Refresh On:   {result.RefreshOn}");
            System.Console.WriteLine($"Token Type:   {result.TokenType}");
        }
        return 0;
    }
}
