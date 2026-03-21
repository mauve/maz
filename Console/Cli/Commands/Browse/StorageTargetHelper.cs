using Console.Cli.Commands.Copy;
using Console.Cli.Http;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Browse;

/// <summary>Shared target parsing and auth for storage browse/query commands.</summary>
internal static class StorageTargetHelper
{
    public readonly record struct ParsedTarget(string Account, string? Container, string? Prefix);

    public static ParsedTarget ParseTarget(string targetRaw)
    {
        if (targetRaw.Contains('/') || targetRaw.Contains('.'))
        {
            var path = CopyPath.Parse(targetRaw);
            if (path.Kind != CopyPathKind.BlobStorage)
                throw new InvocationException(
                    $"Target must be an Azure Storage path, not a local path: '{targetRaw}'"
                );
            return new ParsedTarget(
                path.AccountName!,
                path.ContainerName,
                string.IsNullOrEmpty(path.BlobPrefix) ? null : path.BlobPrefix
            );
        }

        return new ParsedTarget(targetRaw, null, null);
    }

    public static IBlobAuthStrategy BuildAuthStrategy(
        string account,
        string? sasToken,
        string? accountKey,
        AuthOptionPack auth,
        DiagnosticLog log
    )
    {
        if (!string.IsNullOrEmpty(sasToken))
        {
            log.Credential("Using SAS token authentication");
            return new SasBlobAuth(sasToken);
        }

        if (!string.IsNullOrEmpty(accountKey))
        {
            log.Credential($"Using SharedKey authentication for account '{account}'");
            return new SharedKeyBlobAuth(account, accountKey);
        }

        log.Credential("Using token credential (scope: storage.azure.com)");
        var credential = auth.GetCredential(log);
        return new TokenBlobAuth(credential);
    }
}
