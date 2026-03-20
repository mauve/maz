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
    public static void WriteBlobAttributes(string filePath, TransferItem item)
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
