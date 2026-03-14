using Azure.Core;

namespace Console.Cli.Shared;

/// <summary>
/// Wraps a <see cref="TokenCredential"/> and caches acquired tokens until
/// 5 minutes before their expiry, avoiding redundant token acquisitions
/// within a single process lifetime.
/// </summary>
internal sealed class CachingTokenCredential(TokenCredential inner) : TokenCredential
{
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, AccessToken> _cache = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken
    )
    {
        var key = CacheKey(requestContext);
        if (TryGetCached(key, out var cached))
            return cached;

        var token = inner.GetToken(requestContext, cancellationToken);
        Store(key, token);
        return token;
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken
    )
    {
        var key = CacheKey(requestContext);
        if (TryGetCached(key, out var cached))
            return cached;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring the lock
            if (TryGetCached(key, out cached))
                return cached;

            var token = await inner.GetTokenAsync(requestContext, cancellationToken);
            Store(key, token);
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool TryGetCached(string key, out AccessToken token)
    {
        if (
            _cache.TryGetValue(key, out token)
            && token.ExpiresOn > DateTimeOffset.UtcNow + ExpiryBuffer
        )
            return true;

        token = default;
        return false;
    }

    private void Store(string key, AccessToken token) => _cache[key] = token;

    private static string CacheKey(TokenRequestContext ctx)
    {
        var scopes = string.Join(" ", ctx.Scopes);
        return ctx.TenantId is { Length: > 0 } t ? $"{t}\0{scopes}" : scopes;
    }
}
