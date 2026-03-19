using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Console.Cli.Auth;

/// <summary>
/// Builds JWT client assertions for certificate-based service principal auth.
/// </summary>
internal static class ClientAssertionBuilder
{
    public static string BuildFromCertificate(
        string tenant,
        string clientId,
        string certificatePath,
        string? certificatePassword
    )
    {
        var cert = certificatePassword is not null
            ? X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword)
            : X509CertificateLoader.LoadCertificateFromFile(certificatePath);

        var rsa = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Certificate does not contain an RSA private key.");

        var thumbprint = cert.GetCertHash();
        var x5t = Base64UrlEncode(thumbprint);

        var now = DateTimeOffset.UtcNow;
        var header = new
        {
            alg = "RS256",
            typ = "JWT",
            x5t,
        };

        var payload = new
        {
            aud = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            iss = clientId,
            sub = clientId,
            jti = Guid.NewGuid().ToString(),
            nbf = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(10).ToUnixTimeSeconds(),
        };

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var signingInput = $"{headerB64}.{payloadB64}";
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
