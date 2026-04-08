using System.Text;
using System.Text.RegularExpressions;
using Azure.Identity;
using Console.Cli;
using Console.Cli.Auth;
using Console.Rendering;

namespace Console;

internal static partial class AuthenticationErrorFormatter
{
    private static readonly Dictionary<string, string> AadStsDescriptions = new()
    {
        ["AADSTS70043"] =
            "The refresh token has expired due to a Conditional Access sign-in frequency policy.",
        ["AADSTS700082"] = "The refresh token has expired.",
        ["AADSTS50076"] = "Multi-factor authentication is required.",
        ["AADSTS50079"] = "Multi-factor authentication enrollment is required.",
        ["AADSTS70011"] = "The requested scope is invalid.",
        ["AADSTS50173"] =
            "The token was issued before the password was changed. Please re-authenticate.",
        ["AADSTS65001"] = "The application requires consent that has not been granted.",
        ["AADSTS65004"] = "The user did not consent to the application.",
        ["AADSTS7000215"] = "Invalid client secret.",
        ["AADSTS700016"] = "The application was not found in the directory.",
        ["AADSTS50105"] = "The user is not assigned to a role for the application.",
    };

    // Hints for Azure SDK credential types that we cannot control the exception types of.
    // Keyed on substrings that appear in ChainedTokenCredential's aggregated message.
    private static readonly Dictionary<string, (string Name, string[] FixHints)> ExternalCredentialHints =
        new()
        {
            ["Azure CLI"] = (
                "Azure CLI",
                ["Re-authenticate:", "  maz login", "  (or: az logout && az login)"]
            ),
            ["Azure Developer CLI"] = (
                "Azure Developer CLI",
                [
                    "Re-authenticate the Azure Developer CLI:",
                    "  azd auth logout",
                    "  azd auth login",
                ]
            ),
            ["Azure PowerShell"] = (
                "Azure PowerShell",
                ["Re-authenticate in PowerShell:", "  Disconnect-AzAccount", "  Connect-AzAccount"]
            ),
            ["EnvironmentCredential"] = (
                "Environment",
                [
                    "Check that the environment variables are set and valid:",
                    "  AZURE_CLIENT_ID, AZURE_TENANT_ID, and AZURE_CLIENT_SECRET (or AZURE_CLIENT_CERTIFICATE_PATH)",
                ]
            ),
            ["ManagedIdentityCredential"] = (
                "Managed Identity",
                [
                    "Verify the managed identity is assigned to this resource and has the required roles.",
                ]
            ),
            ["InteractiveBrowserCredential"] = (
                "Interactive Browser",
                ["Re-authenticate:", "  maz login"]
            ),
            ["VisualStudioCredential"] = (
                "Visual Studio",
                [
                    "Re-authenticate in Visual Studio: Tools → Options → Azure Service Authentication.",
                ]
            ),
            ["SharedTokenCacheCredential"] = (
                "Shared Token Cache",
                ["The cached token is stale. Re-authenticate using the Azure CLI or Visual Studio."]
            ),
            ["DeviceCodeCredential"] = (
                "Device Code",
                ["Re-authenticate using the device code flow by running the command again."]
            ),
            ["WorkloadIdentityCredential"] = (
                "Workload Identity",
                [
                    "Verify the federated token file is current and the workload identity is configured correctly.",
                ]
            ),
        };

    public static string Format(
        AuthenticationFailedException ex,
        IReadOnlyList<CredentialType>? configuredTypes = null
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine(Ansi.Red(Ansi.Bold("Authentication failed")));
        sb.AppendLine();

        if (ex is BrowserAuthException bae)
            FormatBrowserAuthFailure(sb, bae);
        else
            FormatChainedAuthFailure(sb, ex, configuredTypes);

        return sb.ToString().TrimEnd();
    }

    private static void FormatBrowserAuthFailure(StringBuilder sb, BrowserAuthException ex)
    {
        var entries = new List<(string, string)>();

        entries.Add(("Credential", Ansi.Bold("Browser")));

        if (ex.AadError is not null)
        {
            var errorValue = Ansi.Yellow(ex.AadError);
            entries.Add(("Error", errorValue));
        }

        if (ex.AadStsCode is not null)
        {
            var stsValue = Ansi.Yellow(ex.AadStsCode);
            if (AadStsDescriptions.TryGetValue(ex.AadStsCode, out var stsDesc))
                stsValue += $" \u2014 {stsDesc}";
            entries.Add(("AADSTS", stsValue));
        }

        if (ex.AadErrorDescription is not null && ex.AadErrorDescription != ex.AadError)
            entries.Add(("Detail", Ansi.Dim(TruncateDescription(ex.AadErrorDescription))));

        AppendDefinitionList(sb, entries);
        sb.AppendLine();

        // Contextual remediation based on the typed error
        sb.AppendLine(Ansi.Bold("  To fix:"));
        foreach (var line in GetBrowserFixHints(ex))
            sb.AppendLine($"    {line}");
        sb.AppendLine();
    }

    private static IEnumerable<string> GetBrowserFixHints(BrowserAuthException ex)
    {
        if (ex.IsUserCancellation)
        {
            yield return "You declined the sign-in request.";
            yield return "Run 'maz login' again if this was unintentional.";
            yield break;
        }

        if (ex.IsConsentRequired)
        {
            yield return "The application requires admin consent.";
            yield return "Ask your Azure AD administrator to grant consent,";
            yield return "or configure a custom app registration (see docs/custom-app-registration.md).";
            yield break;
        }

        switch (ex.AadStsCode)
        {
            case "AADSTS50076" or "AADSTS50079":
                yield return "Multi-factor authentication is required.";
                yield return "Run 'maz login' and complete the MFA prompt.";
                yield break;
            case "AADSTS700082" or "AADSTS70043":
                yield return "Your session has expired.";
                yield return "Run:  maz logout && maz login";
                yield break;
            case "AADSTS50173":
                yield return "Your password was changed since this token was issued.";
                yield return "Run:  maz logout && maz login";
                yield break;
            case "AADSTS700016":
                yield return "The application was not found in the directory.";
                yield return "Check the client ID in your maz configuration.";
                yield break;
            case "AADSTS50105":
                yield return "Your account is not assigned to a role for this application.";
                yield return "Ask your administrator to assign you access.";
                yield break;
        }

        yield return "Re-authenticate:";
        yield return "  maz logout";
        yield return "  maz login";
    }

    private static void FormatChainedAuthFailure(
        StringBuilder sb,
        AuthenticationFailedException ex,
        IReadOnlyList<CredentialType>? configuredTypes
    )
    {
        var message = CollectMessages(ex);

        // Extract AADSTS code from the aggregated message — for Azure SDK credentials
        // (CLI, PowerShell, etc.) we cannot control the exception types, so we still
        // parse the message here.
        var aadCode = ExtractAadStsCode(message);

        // Detect which external credential failed based on known message substrings.
        var failedHintKey = DetectExternalCredential(message, configuredTypes);

        var entries = new List<(string, string)>();

        if (failedHintKey is not null && ExternalCredentialHints.TryGetValue(failedHintKey, out var hint))
            entries.Add(("Credential", Ansi.Bold(hint.Name)));

        if (aadCode is not null)
        {
            var errorValue = Ansi.Yellow(aadCode);
            if (AadStsDescriptions.TryGetValue(aadCode, out var desc))
                errorValue += $" \u2014 {desc}";
            entries.Add(("AADSTS", errorValue));
        }

        var tenantId = ExtractTenantId(message);
        if (tenantId is not null)
            entries.Add(("Tenant", Ansi.Dim(tenantId)));

        if (entries.Count > 0)
        {
            AppendDefinitionList(sb, entries);
            sb.AppendLine();
        }

        if (failedHintKey is not null && ExternalCredentialHints.TryGetValue(failedHintKey, out var credHint))
        {
            sb.AppendLine(Ansi.Bold("  To fix:"));
            foreach (var line in credHint.FixHints)
                sb.AppendLine($"    {line}");
            sb.AppendLine();
        }

        // Suggest alternate credential types when the configured chain is known
        if (configuredTypes is { Count: > 0 })
        {
            var alternates = SuggestAlternates(configuredTypes, failedHintKey);
            if (alternates.Count > 0)
            {
                sb.AppendLine(Ansi.Dim("  Alternatively, try a different credential type:"));
                foreach (var alt in alternates)
                    sb.AppendLine(Ansi.Dim($"    --auth-allowed-credential-types {alt}"));
                sb.AppendLine();
            }
        }
    }

    private static void AppendDefinitionList(StringBuilder sb, List<(string, string)> entries)
    {
        using var block = new StringWriter();
        DefinitionList.Write(block, entries);
        sb.Append(block.ToString());
    }

    private static string CollectMessages(Exception? ex)
    {
        var parts = new List<string>();
        while (ex is not null)
        {
            if (!string.IsNullOrWhiteSpace(ex.Message))
                parts.Add(ex.Message);
            ex = ex.InnerException;
        }
        return string.Join(" ", parts);
    }

    private static string? ExtractAadStsCode(string messages)
    {
        var m = AadStsRegex().Match(messages);
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Detects which external (Azure SDK) credential failed by scanning the aggregated
    /// ChainedTokenCredential message for known credential name substrings. This string
    /// matching is limited to credentials whose exception types we cannot control.
    /// </summary>
    private static string? DetectExternalCredential(
        string messages,
        IReadOnlyList<CredentialType>? configuredTypes
    )
    {
        // Walk configured types in reverse — the last credential that failed is most relevant.
        if (configuredTypes is { Count: > 0 })
        {
            for (int i = configuredTypes.Count - 1; i >= 0; i--)
            {
                if (
                    CredentialTypeToHintKey.TryGetValue(configuredTypes[i], out var key)
                    && ExternalCredentialHints.ContainsKey(key)
                    && messages.Contains(key, StringComparison.OrdinalIgnoreCase)
                )
                    return key;
            }
        }

        // Fallback: scan all known hint keys
        foreach (var key in ExternalCredentialHints.Keys)
        {
            if (messages.Contains(key, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return null;
    }

    private static readonly Dictionary<CredentialType, string> CredentialTypeToHintKey = new()
    {
        [CredentialType.Cli] = "Azure CLI",
        [CredentialType.Dev] = "Azure Developer CLI",
        [CredentialType.PowerShell] = "Azure PowerShell",
        [CredentialType.Env] = "EnvironmentCredential",
        [CredentialType.ManagedIdentity] = "ManagedIdentityCredential",
        [CredentialType.Browser] = "InteractiveBrowserCredential",
        [CredentialType.VisualStudio] = "VisualStudioCredential",
        [CredentialType.SharedTokenCache] = "SharedTokenCacheCredential",
        [CredentialType.DeviceCode] = "DeviceCodeCredential",
        [CredentialType.WorkloadIdentity] = "WorkloadIdentityCredential",
    };

    private static string? ExtractTenantId(string messages)
    {
        var m = TenantRegex().Match(messages);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<string> SuggestAlternates(
        IReadOnlyList<CredentialType> configured,
        string? failedHintKey
    )
    {
        static string ToCliValue(CredentialType t) =>
            t switch
            {
                CredentialType.MsalCache => "msalcache",
                CredentialType.Cli => "cli",
                CredentialType.Dev => "dev",
                CredentialType.PowerShell => "ps",
                CredentialType.Env => "env",
                CredentialType.ManagedIdentity => "mi",
                CredentialType.Browser => "browser",
                CredentialType.VisualStudio => "vs",
                CredentialType.SharedTokenCache => "shared",
                CredentialType.DeviceCode => "devicecode",
                CredentialType.WorkloadIdentity => "wid",
                _ => t.ToString().ToLowerInvariant(),
            };

        var failedType = CredentialTypeToHintKey
            .FirstOrDefault(kv => kv.Value == failedHintKey)
            .Key;
        return configured.Where(t => t != failedType).Select(ToCliValue).ToList();
    }

    private static string TruncateDescription(string description)
    {
        // AAD descriptions often contain a long trace ID at the end after "Trace ID:"
        var traceIdx = description.IndexOf("Trace ID:", StringComparison.OrdinalIgnoreCase);
        if (traceIdx > 0)
            description = description[..traceIdx].TrimEnd(' ', '\n', '\r', '.');
        return description;
    }

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadStsRegex();

    [GeneratedRegex(@"--tenant\s+[""']?([0-9a-f\-]{36})[""']?")]
    private static partial Regex TenantRegex();
}
