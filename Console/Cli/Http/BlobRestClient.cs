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
        bool includeTags = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        string? marker = null;
        do
        {
            var url =
                $"https://{account}.blob.core.windows.net/{container}?restype=container&comp=list&maxresults=5000";
            if (includeTags)
                url += "&include=tags";
            if (!string.IsNullOrEmpty(prefix))
                url += $"&prefix={Uri.EscapeDataString(prefix)}";
            if (marker is not null)
                url += $"&marker={Uri.EscapeDataString(marker)}";

            var response = await SendAsync(HttpMethod.Get, url, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var blobs = xml.Descendants("Blob");

            foreach (var blob in blobs)
                yield return ParseBlobItem(blob);

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

        EnsureSuccess(response, url);
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

        return ParseBlobProperties(response);
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
        EnsureSuccess(response, url);

        return ParseBlobProperties(response);
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

        EnsureSuccess(response, url);
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
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}?comp=blocklist";

        var xml = new XElement("BlockList", blockIds.Select(id => new XElement("Latest", id)));

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml");
        request.Headers.Add("x-ms-version", "2024-11-04");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Put, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        EnsureSuccess(response, url);
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

        EnsureSuccess(response, url);
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
        var url = $"https://{destAccount}.blob.core.windows.net/{destContainer}/{destBlob}";

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

        EnsureSuccess(response, url);

        return response.Headers.TryGetValues("x-ms-copy-id", out var ids) ? ids.First() : "";
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

    /// <summary>List containers in a storage account.</summary>
    public async IAsyncEnumerable<ContainerItem> ListContainersAsync(
        string account,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        string? marker = null;
        do
        {
            var url = $"https://{account}.blob.core.windows.net/?comp=list&maxresults=5000";
            if (marker is not null)
                url += $"&marker={Uri.EscapeDataString(marker)}";

            var response = await SendAsync(HttpMethod.Get, url, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            foreach (var container in xml.Descendants("Container"))
            {
                var name = container.Element("Name")?.Value ?? "";
                var props = container.Element("Properties");
                var lastModified = DateTimeOffset.TryParse(
                    props?.Element("Last-Modified")?.Value,
                    out var lm
                )
                    ? lm
                    : (DateTimeOffset?)null;
                yield return new ContainerItem(name, lastModified);
            }

            marker = xml.Descendants("NextMarker").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(marker))
                marker = null;
        } while (marker is not null);
    }

    /// <summary>List blobs and virtual folders using delimiter-based hierarchy.</summary>
    public async IAsyncEnumerable<BlobHierarchyItem> ListBlobsByHierarchyAsync(
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
                $"https://{account}.blob.core.windows.net/{container}?restype=container&comp=list&delimiter=/&maxresults=5000";
            if (!string.IsNullOrEmpty(prefix))
                url += $"&prefix={Uri.EscapeDataString(prefix)}";
            if (marker is not null)
                url += $"&marker={Uri.EscapeDataString(marker)}";

            var response = await SendAsync(HttpMethod.Get, url, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            // Virtual folders (BlobPrefix elements)
            foreach (var bp in xml.Descendants("BlobPrefix"))
            {
                var name = bp.Element("Name")?.Value ?? "";
                yield return new BlobHierarchyItem(name, null);
            }

            // Actual blobs
            foreach (var blob in xml.Descendants("Blob"))
            {
                var item = ParseBlobItem(blob);
                yield return new BlobHierarchyItem(item.Name, item);
            }

            marker = xml.Descendants("NextMarker").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(marker))
                marker = null;
        } while (marker is not null);
    }

    /// <summary>Delete a blob.</summary>
    public async Task DeleteBlobAsync(
        string account,
        string container,
        string blob,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}";
        await SendAsync(HttpMethod.Delete, url, ct);
    }

    /// <summary>Get blob tags as key-value pairs.</summary>
    public async Task<Dictionary<string, string>> GetBlobTagsAsync(
        string account,
        string container,
        string blob,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}?comp=tags";
        var response = await SendAsync(HttpMethod.Get, url, ct);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var tags = new Dictionary<string, string>();
        foreach (var tag in xml.Descendants("Tag"))
        {
            var key = tag.Element("Key")?.Value;
            var value = tag.Element("Value")?.Value;
            if (key is not null)
                tags[key] = value ?? "";
        }

        return tags;
    }

    /// <summary>Set blob tags (replaces all existing tags).</summary>
    public async Task SetBlobTagsAsync(
        string account,
        string container,
        string blob,
        Dictionary<string, string> tags,
        CancellationToken ct
    )
    {
        var url = $"https://{account}.blob.core.windows.net/{container}/{blob}?comp=tags";

        var tagSet = new XElement(
            "Tags",
            new XElement(
                "TagSet",
                tags.Select(kv => new XElement(
                    "Tag",
                    new XElement("Key", kv.Key),
                    new XElement("Value", kv.Value)
                ))
            )
        );

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new StringContent(tagSet.ToString(), Encoding.UTF8, "application/xml");
        request.Headers.Add("x-ms-version", "2024-11-04");
        await _auth.ApplyAsync(request, ct);

        _log.HttpRequest(HttpMethod.Put, url, request);
        var sw = Stopwatch.StartNew();

        var response = await Http.SendAsync(request, ct);

        sw.Stop();
        _log.HttpResponse(response, sw.ElapsedMilliseconds);

        EnsureSuccess(response, url);
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

        EnsureSuccess(response, url);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private static BlobItem ParseBlobItem(XElement blob)
    {
        var name = blob.Element("Name")?.Value ?? "";
        var props = blob.Element("Properties");
        var size = long.TryParse(props?.Element("Content-Length")?.Value, out var s) ? s : 0;
        var lastModified = DateTimeOffset.TryParse(
            props?.Element("Last-Modified")?.Value,
            out var lm
        )
            ? lm
            : (DateTimeOffset?)null;
        var contentType = props?.Element("Content-Type")?.Value;
        var contentMD5 = props?.Element("Content-MD5")?.Value;
        var creationTime = DateTimeOffset.TryParse(
            props?.Element("Creation-Time")?.Value,
            out var ct2
        )
            ? ct2
            : (DateTimeOffset?)null;

        var tagsElement = blob.Element("Tags")?.Element("TagSet");
        Dictionary<string, string>? tags = null;
        if (tagsElement is not null)
        {
            tags = new Dictionary<string, string>();
            foreach (var tag in tagsElement.Elements("Tag"))
            {
                var key = tag.Element("Key")?.Value;
                var value = tag.Element("Value")?.Value;
                if (key is not null)
                    tags[key] = value ?? "";
            }
        }

        return new BlobItem(name, size, lastModified, contentType, tags, contentMD5, creationTime);
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

        EnsureSuccess(response, url);
        return response;
    }

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var vals) ? vals.FirstOrDefault() : null;

    private static BlobProperties ParseBlobProperties(HttpResponseMessage response)
    {
        var size = response.Content.Headers.ContentLength ?? 0;
        var lastModified = response.Content.Headers.LastModified;
        var contentType = response.Content.Headers.ContentType?.ToString();
        var contentMD5 = response.Content.Headers.ContentMD5 is { } md5Bytes
            ? Convert.ToBase64String(md5Bytes)
            : null;
        var etag = response.Headers.ETag?.Tag;
        var cacheControl = response.Headers.CacheControl?.ToString();
        var contentDisposition = response.Content.Headers.ContentDisposition?.ToString();
        var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
        var contentLanguage = response.Content.Headers.ContentLanguage.FirstOrDefault();
        var blobType = GetHeader(response, "x-ms-blob-type");
        var accessTier = GetHeader(response, "x-ms-access-tier");

        return new BlobProperties(
            size,
            lastModified,
            contentType,
            contentMD5,
            etag,
            cacheControl,
            contentDisposition,
            contentEncoding,
            contentLanguage,
            blobType,
            accessTier
        );
    }

    /// <summary>
    /// Like EnsureSuccessStatusCode but includes the URL in the exception
    /// so error messages identify which resource failed.
    /// </summary>
    private static void EnsureSuccess(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode)
            return;

        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? "Unknown";
        throw new HttpRequestException(
            $"Response status code does not indicate success: {status} ({reason}). URL: {url}",
            inner: null,
            statusCode: response.StatusCode
        );
    }
}

/// <summary>A blob item returned from listing.</summary>
public sealed record BlobItem(
    string Name,
    long Size,
    DateTimeOffset? LastModified,
    string? ContentType,
    Dictionary<string, string>? Tags = null,
    string? ContentMD5 = null,
    DateTimeOffset? CreationTime = null
);

/// <summary>Blob properties from a HEAD request.</summary>
public sealed record BlobProperties(
    long Size,
    DateTimeOffset? LastModified,
    string? ContentType,
    string? ContentMD5 = null,
    string? ETag = null,
    string? CacheControl = null,
    string? ContentDisposition = null,
    string? ContentEncoding = null,
    string? ContentLanguage = null,
    string? BlobType = null,
    string? AccessTier = null
);

/// <summary>A blob found by tag query.</summary>
public sealed record BlobTagItem(string Name);

/// <summary>A container in a storage account.</summary>
public sealed record ContainerItem(string Name, DateTimeOffset? LastModified);

/// <summary>An item from hierarchy listing: either a virtual folder prefix or a blob.</summary>
public sealed record BlobHierarchyItem(string Name, BlobItem? Blob)
{
    /// <summary>True when this item is a virtual folder (prefix), not an actual blob.</summary>
    public bool IsPrefix => Blob is null;
}
