using Console.Cli.Http;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Copy;

/// <summary>
/// Builds the list of transfer items by enumerating sources,
/// applying glob/tag filters, and checking overwrite policies.
/// </summary>
public sealed class TransferPlan
{
    private readonly BlobRestClient _client;
    private readonly CopyPath _source;
    private readonly CopyPath _dest;
    private readonly OverwritePolicy _overwritePolicy;
    private readonly GlobMatcher? _includeGlob;
    private readonly GlobMatcher? _excludeGlob;
    private readonly GlobMatcher? _inlineGlob;
    private readonly string? _tagQuery;
    private readonly DiagnosticLog _log;

    public TransferPlan(
        BlobRestClient client,
        CopyPath source,
        CopyPath dest,
        OverwritePolicy overwritePolicy,
        string? includePattern,
        string? excludePattern,
        string? tagQuery,
        DiagnosticLog log
    )
    {
        _client = client;
        _source = source;
        _dest = dest;
        _overwritePolicy = overwritePolicy;
        _includeGlob = includePattern is not null ? new GlobMatcher(includePattern) : null;
        _excludeGlob = excludePattern is not null ? new GlobMatcher(excludePattern) : null;
        _inlineGlob = source.GlobPattern is not null ? new GlobMatcher(source.GlobPattern) : null;
        _tagQuery = tagQuery;
        _log = log;
    }

    /// <summary>Determine the transfer direction based on source and destination kinds.</summary>
    public TransferDirection GetDirection(bool forceClientSide)
    {
        if (_source.Kind == CopyPathKind.Local && _dest.Kind == CopyPathKind.BlobStorage)
            return TransferDirection.Upload;
        if (_source.Kind == CopyPathKind.BlobStorage && _dest.Kind == CopyPathKind.Local)
            return TransferDirection.Download;
        if (_source.Kind == CopyPathKind.BlobStorage && _dest.Kind == CopyPathKind.BlobStorage)
            return forceClientSide ? TransferDirection.ClientSideCopy : TransferDirection.ServerSideCopy;

        throw new InvocationException("At least one side of the copy must be Azure Blob Storage.");
    }

    /// <summary>Build the complete list of transfer items.</summary>
    public async Task<IReadOnlyList<TransferItem>> BuildAsync(
        bool forceClientSide,
        CancellationToken ct
    )
    {
        var direction = GetDirection(forceClientSide);
        _log.Trace($"Transfer direction: {direction}");
        if (_inlineGlob is not null)
            _log.Trace($"Inline glob from path: {_source.GlobPattern}");
        if (_includeGlob is not null)
            _log.Trace($"Include filter active");
        if (_excludeGlob is not null)
            _log.Trace($"Exclude filter active");
        if (_tagQuery is not null)
            _log.Trace($"Tag query: {_tagQuery}");
        _log.Trace($"Overwrite policy: {_overwritePolicy}");

        var items = new List<TransferItem>();

        _log.BeginScope("Enumerating source items");
        switch (direction)
        {
            case TransferDirection.Upload:
                await BuildUploadItemsAsync(items, ct);
                break;
            case TransferDirection.Download:
                await BuildDownloadItemsAsync(items, ct);
                break;
            case TransferDirection.ServerSideCopy:
            case TransferDirection.ClientSideCopy:
                await BuildCopyItemsAsync(items, direction, ct);
                break;
        }
        _log.EndScope();
        _log.Trace($"Enumeration complete: {items.Count} item(s)");

        return items;
    }

    private Task BuildUploadItemsAsync(List<TransferItem> items, CancellationToken ct)
    {
        var localPath = _source.LocalPath!;
        if (!Directory.Exists(localPath) && File.Exists(localPath))
        {
            // Single file upload
            var fi = new FileInfo(localPath);
            var blobName = string.IsNullOrEmpty(_dest.BlobPrefix)
                ? fi.Name
                : $"{_dest.BlobPrefix}/{fi.Name}";
            items.Add(
                new TransferItem(
                    fi.FullName,
                    blobName,
                    fi.Length,
                    fi.LastWriteTimeUtc,
                    TransferDirection.Upload
                )
            );
            return Task.CompletedTask;
        }

        if (!Directory.Exists(localPath))
            throw new InvocationException($"Source path '{localPath}' does not exist.");

        var baseDir = Path.GetFullPath(localPath);
        foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');

            if (!MatchesFilters(relativePath))
                continue;

            var fi = new FileInfo(file);
            var blobName = string.IsNullOrEmpty(_dest.BlobPrefix)
                ? relativePath
                : $"{_dest.BlobPrefix}/{relativePath}";

            items.Add(
                new TransferItem(
                    fi.FullName,
                    blobName,
                    fi.Length,
                    fi.LastWriteTimeUtc,
                    TransferDirection.Upload
                )
            );
        }

        return Task.CompletedTask;
    }

    private async Task BuildDownloadItemsAsync(List<TransferItem> items, CancellationToken ct)
    {
        var localBase = _dest.LocalPath!;

        IAsyncEnumerable<BlobItem> blobs;
        if (!string.IsNullOrEmpty(_tagQuery))
        {
            // Tag-based listing: get names from tag query, then get properties
            blobs = ListBlobsByTagsAsync(ct);
        }
        else
        {
            blobs = _client.ListBlobsAsync(
                _source.AccountName!,
                _source.ContainerName!,
                _source.BlobPrefix,
                ct
            );
        }

        await foreach (var blob in blobs.WithCancellation(ct))
        {
            var relativeName = GetRelativeName(blob.Name, _source.BlobPrefix);
            if (!MatchesFilters(relativeName))
                continue;

            var localFilePath = Path.Combine(localBase, relativeName.Replace('/', Path.DirectorySeparatorChar));

            if (_overwritePolicy == OverwritePolicy.Skip && File.Exists(localFilePath))
            {
                _log.Trace($"Skip (exists): {relativeName}");
                continue;
            }
            if (_overwritePolicy == OverwritePolicy.Newer && File.Exists(localFilePath))
            {
                var existingTime = new FileInfo(localFilePath).LastWriteTimeUtc;
                if (blob.LastModified.HasValue && blob.LastModified.Value <= existingTime)
                {
                    _log.Trace($"Skip (not newer): {relativeName}");
                    continue;
                }
            }

            items.Add(
                new TransferItem(
                    blob.Name,
                    localFilePath,
                    blob.Size,
                    blob.LastModified,
                    TransferDirection.Download
                )
            );
        }
    }

    private async Task BuildCopyItemsAsync(
        List<TransferItem> items,
        TransferDirection direction,
        CancellationToken ct
    )
    {
        var blobs = _client.ListBlobsAsync(
            _source.AccountName!,
            _source.ContainerName!,
            _source.BlobPrefix,
            ct
        );

        await foreach (var blob in blobs.WithCancellation(ct))
        {
            var relativeName = GetRelativeName(blob.Name, _source.BlobPrefix);
            if (!MatchesFilters(relativeName))
                continue;

            var destBlobName = string.IsNullOrEmpty(_dest.BlobPrefix)
                ? relativeName
                : $"{_dest.BlobPrefix}/{relativeName}";

            if (_overwritePolicy is OverwritePolicy.Skip or OverwritePolicy.Newer)
            {
                var existing = await _client.TryGetBlobPropertiesAsync(
                    _dest.AccountName!,
                    _dest.ContainerName!,
                    destBlobName,
                    ct
                );
                if (existing is not null)
                {
                    if (_overwritePolicy == OverwritePolicy.Skip)
                        continue;
                    if (
                        _overwritePolicy == OverwritePolicy.Newer
                        && blob.LastModified.HasValue
                        && existing.LastModified.HasValue
                        && blob.LastModified.Value <= existing.LastModified.Value
                    )
                        continue;
                }
            }

            items.Add(
                new TransferItem(
                    blob.Name,
                    destBlobName,
                    blob.Size,
                    blob.LastModified,
                    direction
                )
            );
        }
    }

    private async IAsyncEnumerable<BlobItem> ListBlobsByTagsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await foreach (
            var tagItem in _client
                .FindBlobsByTagsAsync(
                    _source.AccountName!,
                    _source.ContainerName!,
                    _tagQuery!,
                    ct
                )
                .WithCancellation(ct)
        )
        {
            var props = await _client.GetBlobPropertiesAsync(
                _source.AccountName!,
                _source.ContainerName!,
                tagItem.Name,
                ct
            );
            yield return new BlobItem(tagItem.Name, props.Size, props.LastModified, props.ContentType);
        }
    }

    private bool MatchesFilters(string relativePath)
    {
        if (_inlineGlob is not null && !_inlineGlob.IsMatch(relativePath))
            return false;
        if (_includeGlob is not null && !_includeGlob.IsMatch(relativePath))
            return false;
        if (_excludeGlob is not null && _excludeGlob.IsMatch(relativePath))
            return false;
        return true;
    }

    private static string GetRelativeName(string blobName, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return blobName;
        var trimmed = blobName;
        if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            trimmed = trimmed[prefix.Length..];
        return trimmed.TrimStart('/');
    }
}
