using Console.Cli.Auth;
using Console.Cli.Shared;

namespace Console.Cli.Commands;

/// <summary>Log out of Azure and clear cached tokens.</summary>
/// <remarks>
/// Removes cached tokens from both the az cli MSAL cache (`~/.azure/msal_token_cache.json`)
/// and the shared developer cache (used by Visual Studio, VS Code, and azd).
/// By default, also revokes refresh tokens at Microsoft Entra ID so they cannot be
/// reused even if the cache file is later recovered.
///
/// ### Scoped logout
///
///     maz logout                          # all accounts, both caches
///     maz logout --tenant TENANT-ID       # specific tenant only
///     maz logout --account user@domain    # specific account only
///     maz logout --tenant T --account A   # both filters combined
///
/// ### Token revocation
///
/// By default, refresh tokens are revoked at Microsoft Entra ID before being
/// removed from the local cache. Skip this with `--no-revoke` (faster, but
/// tokens remain valid at the server until they expire).
///
/// ### Shared developer cache
///
/// By default, both the az cli cache and the shared developer cache are cleared.
/// Use `--no-shared` to only clear the az cli cache, leaving Visual Studio,
/// VS Code, and azd sessions intact.
///
///     maz logout --no-shared              # clear az cli cache only
/// </remarks>
public partial class LogoutCommandDef : CommandDef
{
    public override string Name => "logout";

    /// <summary>Remove only accounts from this tenant.</summary>
    [CliOption("--tenant")]
    public partial string? Tenant { get; }

    /// <summary>Remove only this account (UPN or object ID).</summary>
    [CliOption("--account")]
    public partial string? Account { get; }

    /// <summary>Revoke refresh tokens at Microsoft Entra ID.</summary>
    [CliOption("--revoke")]
    public partial bool Revoke { get; } = true;

    /// <summary>Also clear matching entries from the shared developer cache.</summary>
    [CliOption("--shared")]
    public partial bool Shared { get; } = true;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var cache = new MsalCache(log);

        // If revoking, find refresh tokens before removing
        if (Revoke)
        {
            var accounts = cache.GetAccounts();
            var oauth = new OAuth2Client(cache, log);

            foreach (var account in accounts)
            {
                if (
                    Tenant is not null
                    && !string.Equals(account.TenantId, Tenant, StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                if (
                    Account is not null
                    && !string.Equals(account.Username, Account, StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                var rt = cache.FindRefreshToken(account.HomeAccountId);
                if (rt is not null)
                {
                    var tenant = account.TenantId ?? "organizations";
                    log.Credential(
                        $"Revoking refresh token for {account.Username ?? account.HomeAccountId}"
                    );
                    await oauth.RevokeRefreshTokenAsync(tenant, rt, ct);
                }
            }
        }

        var removed = cache.RemoveAccounts(Tenant, Account, Shared);

        // Also clear az cli's profile metadata so `az account show` reflects the logout.
        cache.ClearAzCliProfile(Tenant, Account);

        if (removed.Count == 0)
        {
            System.Console.Out.WriteLine("No matching accounts found in cache.");
        }
        else
        {
            foreach (var account in removed)
            {
                var identity = account.Username ?? account.HomeAccountId;
                var tenantInfo = account.TenantId is not null
                    ? $" (tenant: {account.TenantId})"
                    : "";
                System.Console.Out.WriteLine($"Logged out: {identity}{tenantInfo}");
            }

            if (Shared)
                System.Console.Out.WriteLine("Shared developer cache was also cleared.");
        }

        return 0;
    }
}
