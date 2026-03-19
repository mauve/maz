namespace Console.Cli.Auth;

/// <summary>
/// Detects CI environments via environment variables.
/// All checks are pure env var reads — no I/O, no network.
/// </summary>
internal static class CiEnvironmentDetector
{
    /// <summary>
    /// Returns the detected CI environment, or null if not running in CI.
    /// </summary>
    public static DetectedCiEnvironment? Detect()
    {
        // GitHub Actions with OIDC
        if (
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL")
            )
        )
        {
            return new DetectedCiEnvironment(
                "GitHub Actions (OIDC)",
                CredentialType.WorkloadIdentity
            );
        }

        // GitHub Actions with secret-based auth
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            return new DetectedCiEnvironment("GitHub Actions", CredentialType.Env);
        }

        // Azure Pipelines with OIDC
        if (
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUILD_BUILDID"))
            && !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("SYSTEM_OIDCREQUESTURI")
            )
        )
        {
            return new DetectedCiEnvironment(
                "Azure Pipelines (OIDC)",
                CredentialType.WorkloadIdentity
            );
        }

        // Azure Pipelines with secret-based auth
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUILD_BUILDID")))
        {
            return new DetectedCiEnvironment("Azure Pipelines", CredentialType.Env);
        }

        // Generic CI
        if (
            string.Equals(
                Environment.GetEnvironmentVariable("CI"),
                "true",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return new DetectedCiEnvironment("CI (generic)", CredentialType.Env);
        }

        return null;
    }
}

internal sealed record DetectedCiEnvironment(string Name, CredentialType RecommendedCredential);
