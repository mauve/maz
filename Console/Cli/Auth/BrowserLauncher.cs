using System.Diagnostics;

namespace Console.Cli.Auth;

/// <summary>
/// Opens a URL in the system browser, with WSL detection to open
/// on the Windows host when running inside WSL.
/// </summary>
internal static class BrowserLauncher
{
    private static bool? _isWsl;

    /// <summary>
    /// Opens the URL in the system browser. Returns true on success.
    /// </summary>
    public static bool Open(string url)
    {
        if (IsWsl())
            return OpenInWsl(url);

        return OpenNative(url);
    }

    private static bool IsWsl()
    {
        if (_isWsl.HasValue)
            return _isWsl.Value;

        _isWsl = false;
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            if (File.Exists("/proc/version"))
            {
                var version = File.ReadAllText("/proc/version");
                _isWsl =
                    version.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
                    || version.Contains("WSL", StringComparison.Ordinal);
            }
        }
        catch
        {
            // Not WSL
        }

        return _isWsl.Value;
    }

    private static bool OpenInWsl(string url)
    {
        // Try wslview (from wslu package) — most reliable
        if (TryRun("wslview", url))
            return true;

        // Try sensible-browser
        if (TryRun("sensible-browser", url))
            return true;

        // Try cmd.exe /c start via Windows interop
        if (TryRun("/mnt/c/Windows/System32/cmd.exe", $"/c start {url.Replace("&", "^&")}"))
            return true;

        return false;
    }

    private static bool OpenNative(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRun(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
