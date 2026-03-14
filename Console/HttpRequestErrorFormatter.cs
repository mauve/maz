using System.Net.Http;
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

        var entries = new List<(string, string)>();
        var fixHints = new List<string>();

        if (ex.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            var host = ExtractHostFromChain(ex);
            if (host is not null)
                entries.Add(("Host", Ansi.Yellow(host)));
            entries.Add(("Reason", "Could not resolve host"));

            if (host is not null && IsMangledAzureHost(host))
            {
                fixHints.Add("The URL appears to be missing the https:// scheme prefix.");
            }
            else
            {
                fixHints.Add("Check that the URL is correct and the host is reachable.");
                fixHints.Add("Verify your network connection and DNS settings.");
            }
        }
        else
        {
            entries.Add(("Reason", ex.Message));
            if (ex.InnerException is { } inner && inner.Message != ex.Message)
                entries.Add(("Detail", inner.Message));
        }

        using var block = new StringWriter();
        DefinitionList.Write(block, entries);
        sb.Append(block.ToString());

        if (fixHints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Ansi.Bold("  To fix:"));
            foreach (var hint in fixHints)
                sb.AppendLine($"    {hint}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? ExtractHostFromChain(Exception? ex)
    {
        while (ex is not null)
        {
            var host = ExtractHost(ex.Message);
            if (host is not null)
                return host;
            ex = ex.InnerException;
        }
        return null;
    }

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
