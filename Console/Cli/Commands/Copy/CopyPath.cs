using System.Text.RegularExpressions;

namespace Console.Cli.Commands.Copy;

/// <summary>Kind of a copy path: local filesystem or Azure Blob Storage.</summary>
public enum CopyPathKind
{
    Local,
    BlobStorage,
}

/// <summary>Parsed source or destination for the copy command.</summary>
public sealed partial record CopyPath(
    CopyPathKind Kind,
    string? AccountName,
    string? ContainerName,
    string? BlobPrefix,
    string? LocalPath,
    string? GlobPattern
)
{
    /// <summary>
    /// Parse a user-provided path string into a <see cref="CopyPath"/>.
    /// Supports full blob URLs, shorthand (account/container[/prefix]), and local paths.
    /// </summary>
    public static CopyPath Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        // Full blob URL: https://<account>.blob.core.windows.net/<container>[/<prefix>]
        var urlMatch = BlobUrlRegex().Match(raw);
        if (urlMatch.Success)
        {
            var account = urlMatch.Groups["account"].Value;
            var container = urlMatch.Groups["container"].Value;
            var prefix = urlMatch.Groups["prefix"].Value.TrimStart('/');
            var (cleanPrefix, glob) = SplitGlob(prefix);
            return new CopyPath(CopyPathKind.BlobStorage, account, container, cleanPrefix, null, glob);
        }

        // Local path detection: starts with /, ./, ../, ~, or Windows drive letter
        if (IsLocalPath(raw))
        {
            var expanded = raw.StartsWith('~')
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    raw[1..].TrimStart('/', '\\')
                )
                : raw;
            return new CopyPath(CopyPathKind.Local, null, null, null, expanded, null);
        }

        // Shorthand: account/container[/prefix]
        var parts = raw.Split('/', 3);
        if (parts.Length < 2)
            throw new InvocationException(
                $"Invalid path '{raw}'. Expected a local path, blob URL, or shorthand 'account/container[/prefix]'."
            );

        var shortAccount = parts[0];
        var shortContainer = parts[1];
        var shortPrefix = parts.Length > 2 ? parts[2] : "";
        var (shortCleanPrefix, shortGlob) = SplitGlob(shortPrefix);
        return new CopyPath(
            CopyPathKind.BlobStorage,
            shortAccount,
            shortContainer,
            shortCleanPrefix,
            null,
            shortGlob
        );
    }

    /// <summary>Build the base blob URL for this path (only valid for BlobStorage kind).</summary>
    public string GetBlobUrl(string blobName) =>
        $"https://{AccountName}.blob.core.windows.net/{ContainerName}/{blobName}";

    /// <summary>Build the container base URL for this path.</summary>
    public string GetContainerUrl() =>
        $"https://{AccountName}.blob.core.windows.net/{ContainerName}";

    private static bool IsLocalPath(string raw) =>
        raw is "." or ".."
        || raw.StartsWith('/')
        || raw.StartsWith("./", StringComparison.Ordinal)
        || raw.StartsWith("../", StringComparison.Ordinal)
        || raw.StartsWith('~')
        || (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':');

    /// <summary>
    /// Split a prefix at the first wildcard segment, returning (cleanPrefix, globPattern).
    /// </summary>
    private static (string cleanPrefix, string? glob) SplitGlob(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return ("", null);

        // Find the first segment containing a wildcard
        var segments = prefix.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Contains('*') || segments[i].Contains('?'))
            {
                var clean = string.Join('/', segments[..i]);
                var glob = string.Join('/', segments[i..]);
                return (clean, glob);
            }
        }

        return (prefix, null);
    }

    [GeneratedRegex(
        @"^https?://(?<account>[^.]+)(?:\.privatelink)?\.blob\.core\.windows\.net/(?<container>[^/?]+)(?<prefix>/[^?]*)?",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex BlobUrlRegex();
}
