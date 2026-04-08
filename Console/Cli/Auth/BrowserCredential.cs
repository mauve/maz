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
        catch (AadAuthorizationException ex)
        {
            // AAD actively rejected the request (user denied, consent missing, policy blocked, etc.)
            // Throw AuthenticationFailedException to stop the ChainedTokenCredential chain — there
            // is no point trying another credential when the user or AAD has refused this one.
            throw new BrowserAuthException(ex);
        }
        catch (OAuth2Exception ex)
        {
            // Token exchange failed after a successful redirect (e.g. invalid_grant).
            throw new BrowserAuthException(ex);
        }
        catch (Exception ex) when (ex is not AuthenticationFailedException)
        {
            // Transient issue (timeout, listener error, network) — allow the chain to try
            // the next credential type if one is configured.
            throw new CredentialUnavailableException(
                $"Interactive browser authentication failed: {ex.Message}",
                ex
            );
        }
    }
}
