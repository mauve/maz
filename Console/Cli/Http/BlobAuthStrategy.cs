using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;

namespace Console.Cli.Http;

/// <summary>Applies authentication to blob REST requests.</summary>
public interface IBlobAuthStrategy
{
    Task ApplyAsync(HttpRequestMessage request, CancellationToken ct);
}

/// <summary>Bearer token auth using Azure AD (scope: https://storage.azure.com/.default).</summary>
public sealed class TokenBlobAuth(TokenCredential credential) : IBlobAuthStrategy
{
    private const string StorageScope = "https://storage.azure.com/.default";

    public async Task ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([StorageScope]), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }
}

/// <summary>SAS token auth — appends the token to the query string.</summary>
public sealed class SasBlobAuth(string sasToken) : IBlobAuthStrategy
{
    public Task ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        var sas = sasToken.StartsWith('?') ? sasToken[1..] : sasToken;
        request.RequestUri = new Uri($"{uri}{separator}{sas}");
        return Task.CompletedTask;
    }
}

/// <summary>SharedKey auth using HMAC-SHA256 signature.</summary>
public sealed class SharedKeyBlobAuth(string accountName, string accountKey) : IBlobAuthStrategy
{
    public Task ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        request.Headers.Date = now;
        request.Headers.Add("x-ms-version", "2024-11-04");

        var stringToSign = BuildStringToSign(request, accountName);
        var signature = ComputeSignature(stringToSign, accountKey);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "SharedKey",
            $"{accountName}:{signature}"
        );

        return Task.CompletedTask;
    }

    private static string BuildStringToSign(HttpRequestMessage request, string account)
    {
        var method = request.Method.Method;
        var contentEncoding = "";
        var contentLanguage = "";
        var contentLength =
            request.Content?.Headers.ContentLength is > 0
                ? request.Content.Headers.ContentLength.Value.ToString(CultureInfo.InvariantCulture)
                : "";
        var contentMd5 = "";
        var contentType = request.Content?.Headers.ContentType?.ToString() ?? "";
        var date = "";
        var ifModifiedSince = "";
        var ifMatch = "";
        var ifNoneMatch = "";
        var ifUnmodifiedSince = "";
        var range = "";

        var canonicalHeaders = BuildCanonicalHeaders(request);
        var canonicalResource = BuildCanonicalResource(request, account);

        return string.Join(
            '\n',
            method,
            contentEncoding,
            contentLanguage,
            contentLength,
            contentMd5,
            contentType,
            date,
            ifModifiedSince,
            ifMatch,
            ifNoneMatch,
            ifUnmodifiedSince,
            range,
            canonicalHeaders,
            canonicalResource
        );
    }

    private static string BuildCanonicalHeaders(HttpRequestMessage request)
    {
        var headers = request
            .Headers.Where(h => h.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(h => $"{h.Key.ToLowerInvariant()}:{string.Join(',', h.Value).Trim()}");
        return string.Join('\n', headers);
    }

    private static string BuildCanonicalResource(HttpRequestMessage request, string account)
    {
        var uri = request.RequestUri!;
        var resource = $"/{account}{uri.AbsolutePath}";

        // Parse and sort query parameters
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var queryParams = uri.Query
                .TrimStart('?')
                .Split('&')
                .Select(p =>
                {
                    var kv = p.Split('=', 2);
                    return (
                        Key: Uri.UnescapeDataString(kv[0]).ToLowerInvariant(),
                        Value: kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : ""
                    );
                })
                .OrderBy(p => p.Key, StringComparer.Ordinal);

            foreach (var (key, value) in queryParams)
                resource += $"\n{key}:{value}";
        }

        return resource;
    }

    private static string ComputeSignature(string stringToSign, string key)
    {
        var keyBytes = Convert.FromBase64String(key);
        var dataBytes = Encoding.UTF8.GetBytes(stringToSign);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        return Convert.ToBase64String(hash);
    }
}
