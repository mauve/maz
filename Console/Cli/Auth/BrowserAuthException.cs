using Azure.Identity;

namespace Console.Cli.Auth;

/// <summary>
/// Thrown by <see cref="BrowserCredential"/> when interactive browser
/// authentication definitively fails — either because Entra ID rejected the
/// request (e.g. the user denied consent) or because the token exchange
/// failed after the redirect.
/// <para>
/// Unlike <see cref="CredentialUnavailableException"/>, this stops the
/// <see cref="ChainedTokenCredential"/> chain immediately: retrying a
/// different credential type cannot help when the user or AAD has actively
/// refused the sign-in.
/// </para>
/// </summary>
internal sealed class BrowserAuthException : AuthenticationFailedException
{
    /// <summary>The OAuth2 error code, if returned by Entra ID (e.g. "access_denied").</summary>
    public string? AadError { get; }

    /// <summary>Human-readable error description from Entra ID, if available.</summary>
    public string? AadErrorDescription { get; }

    /// <summary>The AADSTS code embedded in the description, if present (e.g. "AADSTS65001").</summary>
    public string? AadStsCode { get; }

    /// <summary>True when the user explicitly declined the sign-in request.</summary>
    public bool IsUserCancellation => AadError is "access_denied";

    /// <summary>True when admin or user consent is missing for the application.</summary>
    public bool IsConsentRequired =>
        AadError is "consent_required" || AadStsCode is "AADSTS65001" or "AADSTS65004";

    internal BrowserAuthException(AadAuthorizationException inner)
        : base(inner.AadErrorDescription, inner)
    {
        AadError = inner.AadError;
        AadErrorDescription = inner.AadErrorDescription;
        AadStsCode = inner.AadStsCode;
    }

    internal BrowserAuthException(OAuth2Exception inner)
        : base(inner.Description, inner)
    {
        AadError = inner.Error;
        AadErrorDescription = inner.Description;
    }
}
