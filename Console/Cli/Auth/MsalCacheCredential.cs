using Azure.Core;
using Azure.Identity;
using Console.Cli.Shared;

namespace Console.Cli.Auth;

/// <summary>
/// A <see cref="TokenCredential"/> that reads tokens from the MSAL caches
/// (az cli + shared developer cache) directly. If a cached access token is
/// valid, returns it immediately. If expired but a refresh token exists,
/// silently refreshes. Otherwise throws <see cref="CredentialUnavailableException"/>.
/// </summary>
internal sealed class MsalCacheCredential : TokenCredential
{
    private readonly MsalCache _cache;
    private readonly OAuth2Client _oauth;
    private readonly DiagnosticLog _log;

    public MsalCacheCredential(MsalCache cache, OAuth2Client oauth, DiagnosticLog log)
    {
        _cache = cache;
        _oauth = oauth;
        _log = log;
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
        var scope =
            requestContext.Scopes.Length > 0
                ? requestContext.Scopes[0]
                : "https://management.azure.com/.default";

        var tenantId = requestContext.TenantId;

        // 1. Try cached access token
        var cached = _cache.FindAccessToken(scope, tenantId);
        if (cached is not null)
        {
            _log.Credential($"MSAL cache: valid access token for {scope}");
            return new AccessToken(cached.AccessToken, cached.ExpiresOn);
        }

        // 2. Try silent refresh using refresh token
        var accounts = _cache.GetAccounts();
        foreach (var account in accounts)
        {
            // If tenant filter is set, skip non-matching accounts
            if (
                tenantId is not null
                && account.TenantId is not null
                && !string.Equals(account.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
            )
                continue;

            var refreshToken = _cache.FindRefreshToken(account.HomeAccountId);
            if (refreshToken is null)
                continue;

            try
            {
                var tenant = tenantId ?? account.TenantId ?? "organizations";
                _log.Credential(
                    $"MSAL cache: refreshing token for {account.Username ?? account.HomeAccountId}"
                );
                var response = await _oauth.AcquireTokenByRefreshTokenAsync(
                    tenant,
                    refreshToken,
                    scope,
                    ct: cancellationToken
                );

                var expiresOn = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);
                return new AccessToken(response.AccessToken, expiresOn);
            }
            catch (OAuth2Exception ex)
            {
                _log.Credential($"MSAL cache: refresh failed for {account.Username}: {ex.Error}");
                continue;
            }
        }

        throw new CredentialUnavailableException(
            "No valid token or refresh token found in MSAL cache. Run 'maz login' or 'az login' to authenticate."
        );
    }
}
