using System.Text;
using System.Text.RegularExpressions;
using Azure.Identity;
using Console.Cli;
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
        ["AADSTS7000215"] = "Invalid client secret.",
        ["AADSTS700016"] = "The application was not found in the directory.",
    };

    private static readonly Dictionary<string, (string Name, string[] FixHints)> CredentialHints =
        new()
        {
            ["MsalCacheCredential"] = (
                "MSAL Cache",
                ["Re-authenticate:", "  maz logout", "  maz login"]
            ),
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
                ["Re-authenticate via the browser login flow."]
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
        var allMessages = CollectMessages(ex);

        var aadCode = ExtractAadStsCode(allMessages);
        var failedCredential = DetectFailedCredential(allMessages);
        var azCommands = ExtractAzCommands(allMessages).Distinct().ToList();
        var tenantId = ExtractTenantId(allMessages);

        sb.AppendLine(Ansi.Red(Ansi.Bold("Authentication failed")));
        sb.AppendLine();

        // Describe what failed
        var entries = new List<(string, string)>();
        if (
            failedCredential is not null
            && CredentialHints.TryGetValue(failedCredential, out var hint)
        )
            entries.Add(("Credential", Ansi.Bold(hint.Name)));
        if (aadCode is not null)
        {
            var errorValue = Ansi.Yellow(aadCode);
            if (AadStsDescriptions.TryGetValue(aadCode, out var desc))
                errorValue += $" \u2014 {desc}";
            entries.Add(("Error", errorValue));
        }
        if (tenantId is not null)
            entries.Add(("Tenant", Ansi.Dim(tenantId)));

        if (entries.Count > 0)
        {
            using var block = new StringWriter();
            DefinitionList.Write(block, entries);
            sb.Append(block.ToString());
        }

        sb.AppendLine();

        // Specific remediation from the exception (az commands)
        if (azCommands.Count > 0)
        {
            sb.AppendLine(Ansi.Bold("  To fix:"));
            foreach (var cmd in azCommands)
                sb.AppendLine($"    {cmd}");
            sb.AppendLine();
        }
        else if (
            failedCredential is not null
            && CredentialHints.TryGetValue(failedCredential, out var credHint)
        )
        {
            sb.AppendLine(Ansi.Bold("  To fix:"));
            foreach (var line in credHint.FixHints)
                sb.AppendLine($"    {line}");
            sb.AppendLine();
        }

        // Suggest alternate credential types if configured types are known
        if (configuredTypes is { Count: > 0 })
        {
            var alternates = SuggestAlternates(configuredTypes, failedCredential);
            if (alternates.Count > 0)
            {
                sb.AppendLine(Ansi.Dim("  Alternatively, try a different credential type:"));
                foreach (var alt in alternates)
                    sb.AppendLine(Ansi.Dim($"    --auth-allowed-credential-types {alt}"));
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
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

    private static string? DetectFailedCredential(string messages)
    {
        foreach (var key in CredentialHints.Keys)
        {
            if (messages.Contains(key, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }

    private static List<string> ExtractAzCommands(string messages)
    {
        var commands = new List<string>();
        foreach (Match m in AzCommandRegex().Matches(messages))
            commands.Add(m.Value.Trim());
        return commands;
    }

    private static string? ExtractTenantId(string messages)
    {
        var m = TenantRegex().Match(messages);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<string> SuggestAlternates(
        IReadOnlyList<CredentialType> configured,
        string? failedCredential
    )
    {
        // Map CredentialType to the description string used in CLI option values
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

        // Map the detected failed credential string to a CredentialType
        static CredentialType? MatchFailed(string? failed) =>
            failed switch
            {
                "MsalCacheCredential" => CredentialType.MsalCache,
                "Azure CLI" => CredentialType.Cli,
                "Azure Developer CLI" => CredentialType.Dev,
                "Azure PowerShell" => CredentialType.PowerShell,
                "EnvironmentCredential" => CredentialType.Env,
                "ManagedIdentityCredential" => CredentialType.ManagedIdentity,
                "InteractiveBrowserCredential" => CredentialType.Browser,
                "VisualStudioCredential" => CredentialType.VisualStudio,
                "SharedTokenCacheCredential" => CredentialType.SharedTokenCache,
                "DeviceCodeCredential" => CredentialType.DeviceCode,
                "WorkloadIdentityCredential" => CredentialType.WorkloadIdentity,
                _ => null,
            };

        var failedType = MatchFailed(failedCredential);
        return configured.Where(t => t != failedType).Select(ToCliValue).ToList();
    }

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadStsRegex();

    [GeneratedRegex(@"az\s+\S+(?:\s+--\S+(?:\s+""[^""]*"")?)*")]
    private static partial Regex AzCommandRegex();

    [GeneratedRegex(@"--tenant\s+[""']?([0-9a-f\-]{36})[""']?")]
    private static partial Regex TenantRegex();
}
