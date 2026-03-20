using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Console.Cli.Shared;

namespace Console.Cli.Http;

/// <summary>
/// Lightweight HTTP client for Azure Blob Storage REST API.
/// Uses raw HTTP for maximum control over chunked parallel transfers.
/// </summary>
public sealed class BlobRestClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly IBlobAuthStrategy _auth;
    private readonly DiagnosticLog _log;

    public BlobRestClient(IBlobAuthStrategy auth, DiagnosticLog log)
    {
        _auth = auth;
        _log = log;
    }

    /// <summary>List blobs in a container with optional prefix filtering.</summary>
    public async IAsyncEnumerable<BlobItem> ListBlobsAsync(
        string account,
        string container,
        string? prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        string? marker = null;
        do
        {
            var url =
                $"https://{account}.blob.core.windows.net/{container}?restype=container&comp=list&maxresults=5000";
            if (!string.IsNullOrEmpty(prefix))
                url += $"&prefix={Uri.EscapeDataString(prefix)}";
            if (marker is not null)
                url += $"&marker={Uri.EscapeDataString(marker)}";

            var response = await SendAsync(HttpMethod.Get, url, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var blobs = xml.Descendants("Blob");

            foreach (var blob in blobs)
            {
                var name = blob.Element("Name")?.Value ?? "";
                var props = blob.Element("Properties");
                var size = long.TryParse(
                    props?.Element("Content-Length")?.Value,
                    out var s
                )
                    ? s
                    : 0;
                var lastModified = DateTimeOffset.TryParse(
                    props?.Element("Last-Modified")?.Value,
                    out var lm
                )
                    ? lm
                    : (DateTimeOffset?)null;
                var contentType = props?.Element("Content-Type")?.Value;

                yield return new BlobItem(name, size, lastModified, contentType);
            }

            marker = xml.Descendants("NextMarker").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(marker))
                marker = null;
        } while (marker is not null);
    }

    /// <summary>Download a range of bytes from a blob.</summary>
    public async Task<Stream> GetBlobRangeAsync(
        string account,
        string container,
        string blob,
        long offset,
        long length,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
        request.Headers.Add("x-ms-version", "2024-11-04");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Get, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>Get blob properties via HEAD request.</summary>
    public async Task<BlobProperties> GetBlobPropertiesAsync(
        string account,
        string container,
        string blob,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}";
        var response = await SendAsync(HttpMethod.Head, url, ct);

        var size = response.Content.Headers.ContentLength ?? 0;
        var lastModified = response.Content.Headers.LastModified;
        var contentType = response.Content.Headers.ContentType?.ToString();

        return new BlobProperties(size, lastModified, contentType);
    }

    /// <summary>Check if a blob exists. Returns null if not found.</summary>
    public async Task<BlobProperties?> TryGetBlobPropertiesAsync(
        string account,
        string container,
        string blob,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}";
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        request.Headers.Add("x-ms-version", "2024-11-04");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Head, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var size = response.Content.Headers.ContentLength ?? 0;
        var lastModified = response.Content.Headers.LastModified;
        var contentType = response.Content.Headers.ContentType?.ToString();

        return new BlobProperties(size, lastModified, contentType);
    }

    /// <summary>Upload a block for a block blob.</summary>
    public async Task PutBlockAsync(
        string account,
        string container,
        string blob,
        string blockId,
        ReadOnlyMemory<byte> content,
        CancellationToken ct
    )
    {
        var encodedBlockId = Uri.EscapeDataString(blockId);
        var url =
            $"https://{account}.blob.core.windows.net/{container}/{blob}?comp=block&blockid={encodedBlockId}";

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new ReadOnlyMemoryContent(content);
        request.Content.Headers.ContentLength = content.Length;
        request.Headers.Add("x-ms-version", "2024-11-04");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Put, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Commit a list of blocks to finalize a block blob.</summary>
    public async Task PutBlockListAsync(
        string account,
        string container,
        string blob,
        IReadOnlyList<string> blockIds,
        CancellationToken ct
    )
    {
        var url =
            $"https://{account}.blob.core.windows.net/{container}/{blob}?comp=blocklist";

        var xml = new XElement(
            "BlockList",
            blockIds.Select(id => new XElement("Latest", id))
        );

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml");
        request.Headers.Add("x-ms-version", "2024-11-04");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Put, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Upload a small blob in a single PUT request.</summary>
    public async Task PutBlobAsync(
        string account,
        string container,
        string blob,
        ReadOnlyMemory<byte> content,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}";

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new ReadOnlyMemoryContent(content);
        request.Content.Headers.ContentLength = content.Length;
        request.Headers.Add("x-ms-version", "2024-11-04");
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Put, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Start a server-side copy operation.</summary>
    public async Task<string> StartCopyBlobAsync(
        string destAccount,
        string destContainer,
        string destBlob,
        string sourceUrl,
        CancellationToken ct
    )
    {
        var url =
            $"https://{destAccount}.blob.core.windows.net/{destContainer}/{destBlob}";

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Add("x-ms-version", "2024-11-04");
        request.Headers.Add("x-ms-copy-source", sourceUrl);
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Put, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        response.EnsureSuccessStatusCode();

        return response.Headers.TryGetValues("x-ms-copy-id", out var ids)
            ? ids.First()
            : "";
    }

    /// <summary>Poll copy status for a server-side copy.</summary>
    public async Task<string> GetCopyStatusAsync(
        string account,
        string container,
        string blob,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}";
        var response = await SendAsync(HttpMethod.Head, url, ct);

        return response.Headers.TryGetValues("x-ms-copy-status", out var vals)
            ? vals.First()
            : "unknown";
    }

    /// <summary>Find blobs by tag query (WHERE clause syntax).</summary>
    public async IAsyncEnumerable<BlobTagItem> FindBlobsByTagsAsync(
        string account,
        string container,
        string tagQuery,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        string? marker = null;
        do
        {
            var url =
                $"https://{account}.blob.core.windows.net/{container}?restype=container&comp=blobs&where={Uri.EscapeDataString(tagQuery)}";
            if (marker is not null)
                url += $"&marker={Uri.EscapeDataString(marker)}";

            var response = await SendAsync(HttpMethod.Get, url, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            foreach (var blob in xml.Descendants("Blob"))
            {
                var name = blob.Element("Name")?.Value ?? "";
                yield return new BlobTagItem(name);
            }

            marker = xml.Descendants("NextMarker").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(marker))
                marker = null;
        } while (marker is not null);
    }

    /// <summary>Download a full blob as a stream.</summary>
    public async Task<Stream> GetBlobAsync(
        string account,
        string container,
        string blob,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-ms-version", "2024-11-04");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Get, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        CancellationToken ct
    )
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-ms-version", "2024-11-04");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(method, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        response.EnsureSuccessStatusCode();
        return response;
    }
}

/// <summary>A blob item returned from listing.</summary>
public sealed record BlobItem(
    string Name,
    long Size,
    DateTimeOffset? LastModified,
    string? ContentType
);

/// <summary>Blob properties from a HEAD request.</summary>
public sealed record BlobProperties(
    long Size,
    DateTimeOffset? LastModified,
    string? ContentType
);

/// <summary>A blob found by tag query.</summary>
public sealed record BlobTagItem(string Name);
