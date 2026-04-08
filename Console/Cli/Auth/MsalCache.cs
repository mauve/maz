using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Console.Cli.Shared;

namespace Console.Cli.Auth;

/// <summary>
/// Reads and writes the MSAL token cache JSON format directly, compatible with
/// az cli (~/.azure/msal_token_cache.json) and the shared developer cache.
/// No MSAL.NET dependency — uses System.Text.Json for AOT safety.
/// </summary>
internal sealed class MsalCache
{
    private const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly DiagnosticLog _log;

    public MsalCache(DiagnosticLog log)
    {
        _log = log;
    }

    // ── Cache file locations ──────────────────────────────────────────

    public static string AzCliCachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".azure",
            OperatingSystem.IsWindows() ? "msal_token_cache.bin" : "msal_token_cache.json"
        );

    public static string SharedDeveloperCachePath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                );
                return Path.Combine(localAppData, ".IdentityService", "msal.cache");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                ".IdentityService",
                "msal.cache"
            );
        }
    }

    // ── Read ──────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to find a valid cached access token across both caches.
    /// Returns null if no valid token is found.
    /// </summary>
    public CachedToken? FindAccessToken(string scope, string? tenantId)
    {
        // Try az cli cache first, then shared developer cache
        var token = FindAccessTokenInFile(AzCliCachePath, scope, tenantId);
        if (token is not null)
        {
            _log.Credential($"Cache hit (az cli) for {scope}");
            return token;
        }

        token = FindAccessTokenInFile(SharedDeveloperCachePath, scope, tenantId);
        if (token is not null)
            _log.Credential($"Cache hit (shared developer) for {scope}");
        return token;
    }

    /// <summary>
    /// Finds a refresh token for a given home account ID across both caches.
    /// When <paramref name="clientId"/> is provided only refresh tokens issued
    /// to that client registration are considered, preventing cross-client
    /// invalid_grant failures.
    /// </summary>
    public string? FindRefreshToken(string homeAccountId, string? clientId = null)
    {
        var rt = FindRefreshTokenInFile(AzCliCachePath, homeAccountId, clientId);
        if (rt is not null)
        {
            _log.Credential($"Refresh token found (az cli)");
            return rt;
        }

        rt = FindRefreshTokenInFile(SharedDeveloperCachePath, homeAccountId, clientId);
        if (rt is not null)
            _log.Credential($"Refresh token found (shared developer)");
        return rt;
    }

    /// <summary>Returns all accounts from both caches (deduplicated by home_account_id).</summary>
    public List<CachedAccount> GetAccounts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accounts = new List<CachedAccount>();

        foreach (var path in new[] { AzCliCachePath, SharedDeveloperCachePath })
        {
            var data = ReadCacheFile(path);
            if (data is null)
                continue;

            if (data["Account"] is not JsonObject accountSection)
                continue;

            foreach (var (_, node) in accountSection)
            {
                if (node is not JsonObject entry)
                    continue;

                var homeId = entry["home_account_id"]?.GetValue<string>();
                if (homeId is null || !seen.Add(homeId))
                    continue;

                accounts.Add(
                    new CachedAccount(
                        homeId,
                        entry["username"]?.GetValue<string>(),
                        entry["realm"]?.GetValue<string>(),
                        entry["environment"]?.GetValue<string>() ?? "login.microsoftonline.com",
                        entry["authority_type"]?.GetValue<string>()
                    )
                );
            }
        }

        return accounts;
    }

    // ── Write ─────────────────────────────────────────────────────────

    /// <summary>
    /// Writes token response data to both MSAL caches.
    /// </summary>
    public void WriteTokenResponse(OAuth2TokenResponse response, string scope, string clientId)
    {
        WriteToCacheFile(AzCliCachePath, response, scope, clientId);
        WriteToCacheFile(SharedDeveloperCachePath, response, scope, clientId);
    }

    // ── Remove ────────────────────────────────────────────────────────

    /// <summary>
    /// Removes accounts (and their tokens) matching the filter from the az cli cache.
    /// Returns the list of removed accounts.
    /// </summary>
    public List<CachedAccount> RemoveAccounts(
        string? tenantFilter,
        string? accountFilter,
        bool includeSharedCache
    )
    {
        var removed = new List<CachedAccount>();
        removed.AddRange(RemoveFromCacheFile(AzCliCachePath, tenantFilter, accountFilter));
        if (includeSharedCache)
            removed.AddRange(
                RemoveFromCacheFile(SharedDeveloperCachePath, tenantFilter, accountFilter)
            );
        return removed;
    }

    // ── Az CLI profile ─────────────────────────────────────────────────

    /// <summary>
    /// Path to az cli's azureProfile.json which stores subscription metadata.
    /// </summary>
    public static string AzCliProfilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".azure",
            "azureProfile.json"
        );

    /// <summary>
    /// Clears matching accounts from az cli's azureProfile.json.
    /// This file stores subscription/account metadata separately from tokens.
    /// </summary>
    public void ClearAzCliProfile(string? tenantFilter, string? accountFilter)
    {
        try
        {
            var path = AzCliProfilePath;
            if (!File.Exists(path))
                return;

            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (json is null)
                return;

            if (json["subscriptions"] is not System.Text.Json.Nodes.JsonArray subs)
                return;

            var toRemove = new List<System.Text.Json.Nodes.JsonNode>();
            foreach (var sub in subs)
            {
                if (sub is not System.Text.Json.Nodes.JsonObject entry)
                    continue;

                if (tenantFilter is not null)
                {
                    var tid = entry["tenantId"]?.GetValue<string>();
                    if (!string.Equals(tid, tenantFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (accountFilter is not null)
                {
                    var user = entry["user"]?["name"]?.GetValue<string>();
                    if (!string.Equals(user, accountFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                toRemove.Add(sub);
            }

            if (toRemove.Count == 0)
                return;

            foreach (var node in toRemove)
                subs.Remove(node);

            var output = json.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false }
            );
            File.WriteAllText(path, output);
            _log.Credential($"Cleared {toRemove.Count} entries from azureProfile.json");
        }
        catch (Exception ex)
        {
            _log.Credential($"Failed to clear azureProfile.json: {ex.Message}");
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────

    private CachedToken? FindAccessTokenInFile(string path, string scope, string? tenantId)
    {
        var data = ReadCacheFile(path);
        if (data is null)
            return null;

        if (data["AccessToken"] is not JsonObject tokenSection)
            return null;

        var now = DateTimeOffset.UtcNow;

        foreach (var (_, node) in tokenSection)
        {
            if (node is not JsonObject entry)
                continue;

            var target = entry["target"]?.GetValue<string>();
            var realm = entry["realm"]?.GetValue<string>();

            if (
                !string.Equals(target, scope, StringComparison.OrdinalIgnoreCase)
                && !TargetContainsScope(target, scope)
            )
                continue;

            if (
                tenantId is not null
                && !string.Equals(realm, tenantId, StringComparison.OrdinalIgnoreCase)
            )
                continue;

            var expiresOn = ParseUnixSeconds(entry["expires_on"]?.GetValue<string>());
            if (expiresOn is null || expiresOn.Value <= now + ExpiryBuffer)
                continue;

            return new CachedToken(
                entry["secret"]?.GetValue<string>()!,
                expiresOn.Value,
                entry["home_account_id"]?.GetValue<string>(),
                realm
            );
        }

        return null;
    }

    private string? FindRefreshTokenInFile(string path, string homeAccountId, string? clientId = null)
    {
        var data = ReadCacheFile(path);
        if (data is null)
            return null;

        if (data["RefreshToken"] is not JsonObject rtSection)
            return null;

        foreach (var (_, node) in rtSection)
        {
            if (node is not JsonObject entry)
                continue;

            var entryHomeId = entry["home_account_id"]?.GetValue<string>();
            if (!string.Equals(entryHomeId, homeAccountId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (clientId is not null)
            {
                var entryClientId = entry["client_id"]?.GetValue<string>();
                if (!string.Equals(entryClientId, clientId, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return entry["secret"]?.GetValue<string>();
        }

        return null;
    }

    private void WriteToCacheFile(
        string path,
        OAuth2TokenResponse response,
        string scope,
        string clientId
    )
    {
        var data = ReadCacheFile(path) ?? CreateEmptyCacheData();
        var env = "login.microsoftonline.com";

        var homeAccountId = BuildHomeAccountId(response);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        // Access Token
        var atSection = data["AccessToken"]?.AsObject() ?? [];
        data["AccessToken"] = atSection;

        var atKey =
            $"{homeAccountId}-{env}-accesstoken-{clientId}-{response.TenantId ?? ""}-{scope}".ToLowerInvariant();
        atSection[atKey] = new JsonObject
        {
            ["home_account_id"] = homeAccountId,
            ["environment"] = env,
            ["credential_type"] = "AccessToken",
            ["client_id"] = clientId,
            ["secret"] = response.AccessToken,
            ["realm"] = response.TenantId ?? "",
            ["target"] = scope,
            ["cached_at"] = nowUnix,
            ["expires_on"] = (
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + response.ExpiresIn
            ).ToString(),
            ["extended_expires_on"] = (
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + response.ExtendedExpiresIn
            ).ToString(),
        };

        // Refresh Token
        if (response.RefreshToken is not null)
        {
            var rtSection = data["RefreshToken"]?.AsObject() ?? [];
            data["RefreshToken"] = rtSection;

            var rtKey = $"{homeAccountId}-{env}-refreshtoken-{clientId}--".ToLowerInvariant();
            rtSection[rtKey] = new JsonObject
            {
                ["home_account_id"] = homeAccountId,
                ["environment"] = env,
                ["credential_type"] = "RefreshToken",
                ["client_id"] = clientId,
                ["secret"] = response.RefreshToken,
            };
        }

        // Id Token
        if (response.IdToken is not null)
        {
            var idSection = data["IdToken"]?.AsObject() ?? [];
            data["IdToken"] = idSection;

            var idKey =
                $"{homeAccountId}-{env}-idtoken-{clientId}-{response.TenantId ?? ""}".ToLowerInvariant();
            idSection[idKey] = new JsonObject
            {
                ["home_account_id"] = homeAccountId,
                ["environment"] = env,
                ["credential_type"] = "IdToken",
                ["client_id"] = clientId,
                ["secret"] = response.IdToken,
                ["realm"] = response.TenantId ?? "",
            };
        }

        // Account
        var accountSection = data["Account"]?.AsObject() ?? [];
        data["Account"] = accountSection;

        var accountKey = $"{homeAccountId}-{env}".ToLowerInvariant();
        accountSection[accountKey] = new JsonObject
        {
            ["home_account_id"] = homeAccountId,
            ["environment"] = env,
            ["realm"] = response.TenantId ?? "",
            ["local_account_id"] = response.LocalAccountId ?? "",
            ["username"] = response.Username ?? "",
            ["authority_type"] = "MSSTS",
        };

        // AppMetadata
        var appSection = data["AppMetadata"]?.AsObject() ?? [];
        data["AppMetadata"] = appSection;

        var appKey = $"appmetadata-{env}-{clientId}".ToLowerInvariant();
        appSection[appKey] = new JsonObject { ["client_id"] = clientId, ["environment"] = env };

        WriteCacheFile(path, data);
    }

    private List<CachedAccount> RemoveFromCacheFile(
        string path,
        string? tenantFilter,
        string? accountFilter
    )
    {
        var data = ReadCacheFile(path);
        if (data is null)
            return [];

        var removed = new List<CachedAccount>();
        var homeIdsToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Find matching accounts
        if (data["Account"] is JsonObject accountSection)
        {
            var keysToRemove = new List<string>();
            foreach (var (key, node) in accountSection)
            {
                if (node is not JsonObject entry)
                    continue;

                var realm = entry["realm"]?.GetValue<string>();
                var username = entry["username"]?.GetValue<string>();
                var homeId = entry["home_account_id"]?.GetValue<string>();

                if (
                    tenantFilter is not null
                    && !string.Equals(realm, tenantFilter, StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                if (
                    accountFilter is not null
                    && !string.Equals(username, accountFilter, StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                keysToRemove.Add(key);
                if (homeId is not null)
                    homeIdsToRemove.Add(homeId);

                removed.Add(new CachedAccount(homeId ?? "", username, realm, "", null));
            }

            foreach (var key in keysToRemove)
                accountSection.Remove(key);
        }

        if (homeIdsToRemove.Count == 0)
            return removed;

        // Remove associated tokens
        RemoveEntriesByHomeAccountId(data, "AccessToken", homeIdsToRemove);
        RemoveEntriesByHomeAccountId(data, "RefreshToken", homeIdsToRemove);
        RemoveEntriesByHomeAccountId(data, "IdToken", homeIdsToRemove);

        WriteCacheFile(path, data);
        return removed;
    }

    private static void RemoveEntriesByHomeAccountId(
        JsonObject data,
        string section,
        HashSet<string> homeIds
    )
    {
        if (data[section] is not JsonObject sectionObj)
            return;

        var keysToRemove = new List<string>();
        foreach (var (key, node) in sectionObj)
        {
            if (node is not JsonObject entry)
                continue;
            var homeId = entry["home_account_id"]?.GetValue<string>();
            if (homeId is not null && homeIds.Contains(homeId))
                keysToRemove.Add(key);
        }

        foreach (var key in keysToRemove)
            sectionObj.Remove(key);
    }

    // ── File I/O with encryption ──────────────────────────────────────

    private JsonObject? ReadCacheFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
                return null;

            // On Windows, az cli cache (.bin) is DPAPI-encrypted
            if (OperatingSystem.IsWindows() && path.EndsWith(".bin", StringComparison.Ordinal))
                bytes = Dpapi.Unprotect(bytes);

            return JsonNode.Parse(bytes)?.AsObject();
        }
        catch (Exception ex)
        {
            _log.Credential($"Failed to read cache {path}: {ex.Message}");
            return null;
        }
    }

    private void WriteCacheFile(string path, JsonObject data)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = data.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);

            // On Windows, az cli cache (.bin) is DPAPI-encrypted
            if (OperatingSystem.IsWindows() && path.EndsWith(".bin", StringComparison.Ordinal))
                bytes = Dpapi.Protect(bytes);

            File.WriteAllBytes(path, bytes);

            // On Unix, restrict file permissions
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            _log.Credential($"Cache written: {path}");
        }
        catch (Exception ex)
        {
            _log.Credential($"Failed to write cache {path}: {ex.Message}");
        }
    }

    private static JsonObject CreateEmptyCacheData() =>
        new()
        {
            ["AccessToken"] = new JsonObject(),
            ["RefreshToken"] = new JsonObject(),
            ["IdToken"] = new JsonObject(),
            ["Account"] = new JsonObject(),
            ["AppMetadata"] = new JsonObject(),
        };

    private static string BuildHomeAccountId(OAuth2TokenResponse response)
    {
        // MSAL format: {uid}.{utid} parsed from the id_token or client_info
        if (response.ClientInfo is not null)
        {
            try
            {
                var json = JsonNode.Parse(Convert.FromBase64String(PadBase64(response.ClientInfo)));
                var uid = json?["uid"]?.GetValue<string>();
                var utid = json?["utid"]?.GetValue<string>();
                if (uid is not null && utid is not null)
                    return $"{uid}.{utid}";
            }
            catch
            {
                // Fall through to other methods
            }
        }

        // Fallback: use oid.tid from id_token claims
        if (response.LocalAccountId is not null && response.TenantId is not null)
            return $"{response.LocalAccountId}.{response.TenantId}";

        return response.LocalAccountId ?? Guid.NewGuid().ToString("N");
    }

    private static bool TargetContainsScope(string? target, string scope)
    {
        if (target is null)
            return false;

        // MSAL stores multiple scopes space-separated in "target"
        foreach (var part in target.Split(' '))
        {
            if (string.Equals(part, scope, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static DateTimeOffset? ParseUnixSeconds(string? value)
    {
        if (value is null || !long.TryParse(value, out var seconds))
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static string PadBase64(string input)
    {
        var mod = input.Length % 4;
        if (mod > 0)
            input += new string('=', 4 - mod);
        return input.Replace('-', '+').Replace('_', '/');
    }
}

// ── Supporting types ──────────────────────────────────────────────────

internal sealed record CachedToken(
    string AccessToken,
    DateTimeOffset ExpiresOn,
    string? HomeAccountId,
    string? TenantId
);

internal sealed record CachedAccount(
    string HomeAccountId,
    string? Username,
    string? TenantId,
    string Environment,
    string? AuthorityType
);
