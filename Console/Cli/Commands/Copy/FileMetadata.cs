using System.Runtime.InteropServices;
using System.Text;

namespace Console.Cli.Commands.Copy;

/// <summary>
/// Writes blob metadata as extended attributes on downloaded files.
/// Uses xattr on Linux/macOS and NTFS Alternate Data Streams on Windows.
/// </summary>
internal static partial class FileMetadata
{
    /// <summary>
    /// Store blob origin metadata on a downloaded file. Best-effort — silently
    /// ignored if the filesystem doesn't support extended attributes.
    /// </summary>
    public static void WriteBlobAttributes(
        string filePath,
        TransferItem item,
        bool saveProperties = false
    )
    {
        try
        {
            if (item.SourceAccountName is not null && item.SourceContainerName is not null)
            {
                var url =
                    $"https://{item.SourceAccountName}.blob.core.windows.net/"
                    + $"{item.SourceContainerName}/{item.SourcePath}";
                SetAttribute(filePath, "maz.blob.url", url);
            }

            if (item.ContentType is not null)
                SetAttribute(filePath, "maz.blob.content-type", item.ContentType);

            // Blob index tags — always written when present
            if (item.Tags is not null)
            {
                foreach (var (key, value) in item.Tags)
                    SetAttribute(filePath, $"maz.blob.tag.{key}", value);
            }

            // Extended properties — opt-in via --save-properties
            if (saveProperties && item.ExtendedProperties is { } props)
            {
                if (props.ContentMD5 is not null)
                    SetAttribute(filePath, "maz.blob.content-md5", props.ContentMD5);
                if (props.ETag is not null)
                    SetAttribute(filePath, "maz.blob.etag", props.ETag);
                if (props.LastModified is { } lm)
                    SetAttribute(filePath, "maz.blob.last-modified", lm.ToString("O"));
                if (props.CacheControl is not null)
                    SetAttribute(filePath, "maz.blob.cache-control", props.CacheControl);
                if (props.ContentDisposition is not null)
                    SetAttribute(
                        filePath,
                        "maz.blob.content-disposition",
                        props.ContentDisposition
                    );
                if (props.ContentEncoding is not null)
                    SetAttribute(filePath, "maz.blob.content-encoding", props.ContentEncoding);
                if (props.ContentLanguage is not null)
                    SetAttribute(filePath, "maz.blob.content-language", props.ContentLanguage);
                if (props.BlobType is not null)
                    SetAttribute(filePath, "maz.blob.blob-type", props.BlobType);
                if (props.AccessTier is not null)
                    SetAttribute(filePath, "maz.blob.access-tier", props.AccessTier);
            }
        }
        catch
        {
            // Filesystem may not support xattr/ADS (e.g. FAT32, network shares)
        }
    }

    private static void SetAttribute(string filePath, string name, string value)
    {
        var data = Encoding.UTF8.GetBytes(value);

        if (OperatingSystem.IsWindows())
            SetAds(filePath, name, data);
        else
            SetXattr(filePath, name, data);
    }

    // ── Windows: NTFS Alternate Data Streams ──────────────────────────────

    private static void SetAds(string filePath, string name, byte[] data)
    {
        // ADS accessed via "filepath:streamname" syntax
        using var fs = new FileStream(
            $"{filePath}:{name}",
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );
        fs.Write(data);
    }

    // ── Linux / macOS: xattr ──────────────────────────────────────────────

    private static void SetXattr(string filePath, string name, byte[] data)
    {
        // Linux requires the "user." namespace prefix
        var attrName = OperatingSystem.IsLinux() ? $"user.{name}" : name;

        int result;
        if (OperatingSystem.IsMacOS())
            result = DarwinSetXattr(filePath, attrName, data, (nuint)data.Length, 0, 0);
        else
            result = LinuxSetXattr(filePath, attrName, data, (nuint)data.Length, 0);

        if (result != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            // ENOTSUP (95 Linux, 45 macOS) — filesystem doesn't support xattr
            // EACCES (13) — permission denied
            // Silently ignore these; throw for unexpected errors
            if (errno is not (95 or 45 or 13))
                throw new InvalidOperationException($"setxattr failed with errno {errno}");
        }
    }

#pragma warning disable CA2101 // marshaling is explicitly UTF-8 via LPUTF8Str
    // macOS: int setxattr(path, name, value, size, position, options)
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int DarwinSetXattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value,
        nuint size,
        uint position,
        int options
    );

    // Linux: int setxattr(path, name, value, size, flags)
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int LinuxSetXattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value,
        nuint size,
        int flags
    );
#pragma warning restore CA2101
}
