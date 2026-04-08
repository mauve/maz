using System.Text.RegularExpressions;

namespace Console.Cli.Auth;

/// <summary>
/// Thrown when Microsoft Entra ID returns an OAuth2 error in the browser
/// redirect — for example, when the user declines consent, the application
/// lacks required permissions, or a Conditional Access policy blocks sign-in.
/// </summary>
internal sealed partial class AadAuthorizationException : Exception
{
    /// <summary>The OAuth2 error code returned by Entra ID (e.g. "access_denied").</summary>
    public string AadError { get; }

    /// <summary>The human-readable description returned by Entra ID.</summary>
    public string AadErrorDescription { get; }

    /// <summary>The AADSTS code embedded in the description, if present (e.g. "AADSTS65001").</summary>
    public string? AadStsCode { get; }

    /// <summary>True when the user explicitly declined the sign-in request.</summary>
    public bool IsUserCancellation => AadError is "access_denied";

    /// <summary>True when admin or user consent is missing for the application.</summary>
    public bool IsConsentRequired =>
        AadError is "consent_required" || AadStsCode is "AADSTS65001" or "AADSTS65004";

    public AadAuthorizationException(string error, string description)
        : base($"AAD authorization failed ({error}): {description}")
    {
        AadError = error;
        AadErrorDescription = description;
        var m = AadStsCodeRegex().Match(description);
        AadStsCode = m.Success ? m.Value : null;
    }

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadStsCodeRegex();
}
