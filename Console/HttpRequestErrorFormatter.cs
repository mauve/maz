using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Console.Rendering;

namespace Console;

internal static class HttpRequestErrorFormatter
{
    public static string Format(HttpRequestException ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Ansi.Red(Ansi.Bold("Network request failed")));
        sb.AppendLine();

        var socketEx = FindSocketException(ex);

        if (socketEx is not null && IsNameResolutionFailure(socketEx))
        {
            var host = ExtractHost(socketEx.Message);
            sb.Append("  Could not resolve host");
            if (host is not null)
                sb.Append($": {Ansi.Yellow(host)}");
            sb.AppendLine();

            if (host is not null && IsMangledAzureHost(host))
            {
                sb.AppendLine();
                sb.AppendLine(
                    Ansi.Dim("  (The URL appears to be missing the https:// scheme prefix.)")
                );
            }
        }
        else
        {
            sb.AppendLine($"  {ex.Message}");
            if (ex.InnerException is { } inner && inner.Message != ex.Message)
                sb.AppendLine($"  {inner.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    private static SocketException? FindSocketException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is SocketException s)
                return s;
            ex = ex.InnerException;
        }
        return null;
    }

    private static bool IsNameResolutionFailure(SocketException ex) =>
        ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData
            || ex.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts the host from messages like "Name or service not known (host:port)".</summary>
    private static string? ExtractHost(string message)
    {
        var start = message.IndexOf('(');
        var end = message.IndexOf(')');
        if (start < 0 || end <= start)
            return null;

        var hostPort = message[(start + 1)..end];
        var colon = hostPort.LastIndexOf(':');
        return colon > 0 ? hostPort[..colon] : hostPort;
    }

    /// <summary>
    /// Returns true when the host looks like it was formed by concatenating an Azure base URL
    /// (e.g. management.azure.com) with a path that was missing the https:// prefix,
    /// producing something like "management.azure.comsmurf".
    /// </summary>
    private static bool IsMangledAzureHost(string host) =>
        host.Contains("azure.com", StringComparison.OrdinalIgnoreCase)
        && !host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase)
        && !host.EndsWith(".azure.net", StringComparison.OrdinalIgnoreCase);
}
