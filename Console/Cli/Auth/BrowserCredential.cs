using Azure.Core;
using Azure.Identity;
using Console.Cli.Shared;

namespace Console.Cli.Auth;

/// <summary>
/// A <see cref="TokenCredential"/> that acquires tokens via the browser using
/// <see cref="OAuth2Client"/> (authorization code + PKCE). Shows the same
/// maz-branded success page as <c>maz login</c>.
/// </summary>
internal sealed class BrowserCredential : TokenCredential
{
    private readonly OAuth2Client _oauth;
    private readonly DiagnosticLog _log;
    private readonly string _tenant;

    public BrowserCredential(OAuth2Client oauth, DiagnosticLog log, string? tenant = null)
    {
        _oauth = oauth;
        _log = log;
        _tenant = tenant ?? "organizations";
    }

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken
    )
    {
        return GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken
    )
    {
        var scopes =
            requestContext.Scopes.Length > 0
                ? requestContext.Scopes
                : ["https://management.azure.com/.default"];

        var tenant = requestContext.TenantId ?? _tenant;

        try
        {
            var response = await _oauth.AcquireTokenInteractiveAsync(
                tenant,
                scopes,
                cancellationToken
            );

            var expiresOn = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);
            return new AccessToken(response.AccessToken, expiresOn);
        }
        catch (Exception ex) when (ex is not CredentialUnavailableException)
        {
            throw new CredentialUnavailableException(
                $"Interactive browser authentication failed: {ex.Message}",
                ex
            );
        }
    }
}
