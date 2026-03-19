using System.Runtime.InteropServices;

namespace Console.Cli.Auth;

/// <summary>
/// Minimal DPAPI wrapper via P/Invoke to CryptProtectData/CryptUnprotectData.
/// Windows-only. Used to encrypt/decrypt az cli's msal_token_cache.bin.
/// </summary>
internal static class Dpapi
{
    public static byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");

        var inputBlob = new DATA_BLOB
        {
            cbData = plaintext.Length,
            pbData = Marshal.AllocHGlobal(plaintext.Length),
        };
        Marshal.Copy(plaintext, 0, inputBlob.pbData, plaintext.Length);

        var outputBlob = new DATA_BLOB();
        try
        {
            if (
                !CryptProtectData(
                    ref inputBlob,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    ref outputBlob
                )
            )
                throw new InvalidOperationException(
                    $"CryptProtectData failed: {Marshal.GetLastWin32Error()}"
                );

            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, outputBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inputBlob.pbData);
            if (outputBlob.pbData != IntPtr.Zero)
                LocalFree(outputBlob.pbData);
        }
    }

    public static byte[] Unprotect(byte[] encrypted)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");

        var inputBlob = new DATA_BLOB
        {
            cbData = encrypted.Length,
            pbData = Marshal.AllocHGlobal(encrypted.Length),
        };
        Marshal.Copy(encrypted, 0, inputBlob.pbData, encrypted.Length);

        var outputBlob = new DATA_BLOB();
        try
        {
            if (
                !CryptUnprotectData(
                    ref inputBlob,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    ref outputBlob
                )
            )
                throw new InvalidOperationException(
                    $"CryptUnprotectData failed: {Marshal.GetLastWin32Error()}"
                );

            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, outputBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inputBlob.pbData);
            if (outputBlob.pbData != IntPtr.Zero)
                LocalFree(outputBlob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut
    );

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        string? ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut
    );

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
