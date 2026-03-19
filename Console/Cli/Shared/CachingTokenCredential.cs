using Azure.Core;

namespace Console.Cli.Shared;

/// <summary>
/// Wraps a <see cref="TokenCredential"/> and caches acquired tokens until
/// 5 minutes before their expiry, avoiding redundant token acquisitions
/// within a single process lifetime.
/// </summary>
internal sealed class CachingTokenCredential : TokenCredential
{
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _inner;
    private readonly DiagnosticLog _log;
    private readonly Dictionary<string, AccessToken> _cache = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CachingTokenCredential(TokenCredential inner, DiagnosticLog log)
    {
        _inner = inner;
        _log = log;
    }

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken
    )
    {
        var key = CacheKey(requestContext);
        if (TryGetCached(key, out var cached))
        {
            _log.Credential($"Token cache hit for {requestContext.Scopes[0]}");
            return cached;
        }

        _log.Credential($"Token cache miss for {requestContext.Scopes[0]}, acquiring...");
        var token = _inner.GetToken(requestContext, cancellationToken);
        Store(key, token);
        _log.Credential($"Token acquired, expires {token.ExpiresOn:yyyy-MM-ddTHH:mm:ssZ}");
        return token;
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken
    )
    {
        var key = CacheKey(requestContext);
        if (TryGetCached(key, out var cached))
        {
            _log.Credential($"Token cache hit for {requestContext.Scopes[0]}");
            return cached;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring the lock
            if (TryGetCached(key, out cached))
            {
                _log.Credential($"Token cache hit for {requestContext.Scopes[0]}");
                return cached;
            }

            _log.Credential($"Token cache miss for {requestContext.Scopes[0]}, acquiring...");
            var token = await _inner.GetTokenAsync(requestContext, cancellationToken);
            Store(key, token);
            _log.Credential($"Token acquired, expires {token.ExpiresOn:yyyy-MM-ddTHH:mm:ssZ}");
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

        // Fallback: when requesting without a TenantId, accept a cached token
        // that was acquired for the same scopes with a specific tenant (e.g. from
        // ArmClient's auth-challenge flow). The token is still valid for the scope.
        if (!key.Contains('\0'))
        {
            var suffix = "\0" + key;
            foreach (var (k, v) in _cache)
            {
                if (
                    k.EndsWith(suffix, StringComparison.Ordinal)
                    && v.ExpiresOn > DateTimeOffset.UtcNow + ExpiryBuffer
                )
                {
                    token = v;
                    return true;
                }
            }
        }

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
