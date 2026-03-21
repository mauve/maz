using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Channels;
using Console.Cli.Http;

namespace Console.Cli.Commands.Copy;

/// <summary>
/// Parallel chunked transfer engine with two-level parallelism:
/// blob-level (--parallel N) and block-level (4 concurrent blocks per blob).
/// Accepts items via a channel so transfers start as blobs are discovered.
/// </summary>
public sealed class BlockTransferEngine
{
    private readonly BlobRestClient _client;
    private readonly CopyPath _dest;
    private readonly int _parallelism;
    private readonly int _blockSize;
    private readonly int _blocksPerBlob;
    private readonly CopyJournal? _journal;
    private readonly bool _saveProperties;
    private readonly bool _verify;
    private readonly Channel<TransferProgressEvent> _progressChannel;
    private readonly CancellationTokenSource _globalCts;
    private readonly ChannelReader<TransferItem> _itemSource;

    // Thread-safe growing collections — items are only added, never removed.
    private readonly object _lock = new();
    private readonly List<TransferItem> _items = [];
    private readonly List<CancellationTokenSource> _perTransferCts = [];

    /// <summary>Channel reader for progress events consumed by TUI or NDJSON output.</summary>
    public ChannelReader<TransferProgressEvent> Progress => _progressChannel.Reader;

    /// <summary>Whether source enumeration has completed.</summary>
    public bool EnumerationComplete { get; private set; }

    /// <summary>Number of items discovered so far.</summary>
    public int ItemCount
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Get a discovered item by index.</summary>
    public TransferItem GetItem(int index)
    {
        lock (_lock)
        {
            return _items[index];
        }
    }

    public BlockTransferEngine(
        BlobRestClient client,
        CopyPath dest,
        ChannelReader<TransferItem> itemSource,
        int parallelism,
        int blockSize,
        CopyJournal? journal,
        CancellationToken ct,
        bool saveProperties = false,
        bool verify = false
    )
    {
        _client = client;
        _dest = dest;
        _itemSource = itemSource;
        _parallelism = parallelism;
        _blockSize = blockSize;
        _blocksPerBlob = 4;
        _journal = journal;
        _saveProperties = saveProperties;
        _verify = verify;
        _progressChannel = Channel.CreateUnbounded<TransferProgressEvent>(
            new UnboundedChannelOptions { SingleReader = true }
        );
        _globalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>
    /// Run all transfers. Reads items from the channel as they arrive
    /// and starts transfers immediately, respecting the parallelism limit.
    /// Returns when all items are transferred and enumeration is complete.
    /// </summary>
    public async Task RunAsync()
    {
        var semaphore = new SemaphoreSlim(_parallelism, _parallelism);
        var tasks = new List<Task>();

        try
        {
            await foreach (var item in _itemSource.ReadAllAsync(_globalCts.Token))
            {
                // Skip items already completed in a prior session
                if (_journal is not null && _journal.IsItemCompleted(item.SourcePath))
                {
                    int skippedIndex;
                    lock (_lock)
                    {
                        skippedIndex = _items.Count;
                        _items.Add(item);
                        _perTransferCts.Add(
                            CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token)
                        );
                    }
                    _progressChannel.Writer.TryWrite(
                        new TransferProgressEvent(
                            skippedIndex,
                            item.Size,
                            item.Size,
                            TransferStatus.Completed,
                            null
                        )
                    );
                    continue;
                }

                int index;
                lock (_lock)
                {
                    index = _items.Count;
                    _items.Add(item);
                    _perTransferCts.Add(
                        CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token)
                    );
                }

                var capturedIndex = index;
                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            await semaphore.WaitAsync(_globalCts.Token);
                            try
                            {
                                await TransferOneAsync(capturedIndex);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        },
                        _globalCts.Token
                    )
                );
            }

            EnumerationComplete = true;
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _progressChannel.Writer.TryComplete();
        }
    }

    /// <summary>Pause a specific transfer.</summary>
    public void Pause(int index)
    {
        CancellationTokenSource? cts;
        TransferItem? item;
        lock (_lock)
        {
            if (index < 0 || index >= _perTransferCts.Count)
                return;
            cts = _perTransferCts[index];
            item = _items[index];
        }
        cts.Cancel();
        _progressChannel.Writer.TryWrite(
            new TransferProgressEvent(index, 0, item.Size, TransferStatus.Paused, null)
        );
    }

    /// <summary>Cancel a specific transfer.</summary>
    public void Cancel(int index)
    {
        CancellationTokenSource? cts;
        TransferItem? item;
        lock (_lock)
        {
            if (index < 0 || index >= _perTransferCts.Count)
                return;
            cts = _perTransferCts[index];
            item = _items[index];
        }
        cts.Cancel();
        _progressChannel.Writer.TryWrite(
            new TransferProgressEvent(index, 0, item.Size, TransferStatus.Cancelled, null)
        );
    }

    /// <summary>Cancel all transfers.</summary>
    public void CancelAll() => _globalCts.Cancel();

    private async Task TransferOneAsync(int index)
    {
        TransferItem item;
        CancellationToken ct;
        lock (_lock)
        {
            item = _items[index];
            ct = _perTransferCts[index].Token;
        }

        _progressChannel.Writer.TryWrite(
            new TransferProgressEvent(index, 0, item.Size, TransferStatus.InProgress, null)
        );

        try
        {
            switch (item.Direction)
            {
                case TransferDirection.Upload:
                    await UploadAsync(index, item, ct);
                    break;
                case TransferDirection.Download:
                    await DownloadAsync(index, item, ct);
                    FileMetadata.WriteBlobAttributes(item.DestPath, item, _saveProperties);
                    if (_verify)
                    {
                        if (item.ContentMD5 is not null)
                            await VerifyAsync(index, item, ct);
                        else
                            System.Console.Error.WriteLine(
                                $"warning: '{item.SourcePath}' has no Content-MD5, skipping verification"
                            );
                    }
                    break;
                case TransferDirection.ServerSideCopy:
                    await ServerSideCopyAsync(index, item, ct);
                    break;
                case TransferDirection.ClientSideCopy:
                    await ClientSideCopyAsync(index, item, ct);
                    break;
            }

            _progressChannel.Writer.TryWrite(
                new TransferProgressEvent(
                    index,
                    item.Size,
                    item.Size,
                    TransferStatus.Completed,
                    null
                )
            );
            _journal?.WriteItemCompleted(item.SourcePath, item.Size);
        }
        catch (OperationCanceledException)
        {
            // Paused or cancelled — already reported
        }
        catch (Exception ex)
        {
            _progressChannel.Writer.TryWrite(
                new TransferProgressEvent(index, 0, item.Size, TransferStatus.Failed, ex.Message)
            );
        }
    }

    private async Task UploadAsync(int index, TransferItem item, CancellationToken ct)
    {
        var fileBytes = await File.ReadAllBytesAsync(item.SourcePath, ct);

        if (fileBytes.Length <= _blockSize)
        {
            await RetryAsync(
                () =>
                    _client.PutBlobAsync(
                        _dest.AccountName!,
                        _dest.ContainerName!,
                        item.DestPath,
                        fileBytes,
                        ct
                    ),
                ct
            );
            ReportProgress(index, fileBytes.Length, item.Size);
            return;
        }

        // Chunked upload
        var blockCount = (int)Math.Ceiling((double)fileBytes.Length / _blockSize);
        var blockIds = new string[blockCount];
        var completedBlocks = _journal?.GetCompletedBlocks(item.SourcePath) ?? new HashSet<int>();
        var uploadSemaphore = new SemaphoreSlim(_blocksPerBlob, _blocksPerBlob);
        long totalUploaded = completedBlocks.Count * (long)_blockSize;

        // Report already-completed progress from journal
        if (totalUploaded > 0)
            ReportProgress(index, Math.Min(totalUploaded, item.Size), item.Size);

        var blockTasks = new Task[blockCount];
        for (int b = 0; b < blockCount; b++)
        {
            var blockIndex = b;
            blockIds[blockIndex] = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(blockIndex.ToString("D6"))
            );

            if (completedBlocks.Contains(blockIndex))
            {
                blockTasks[blockIndex] = Task.CompletedTask;
                continue;
            }

            blockTasks[blockIndex] = Task.Run(
                async () =>
                {
                    await uploadSemaphore.WaitAsync(ct);
                    try
                    {
                        var offset = blockIndex * _blockSize;
                        var length = Math.Min(_blockSize, fileBytes.Length - offset);
                        var chunk = fileBytes.AsMemory(offset, length);

                        await RetryAsync(
                            () =>
                                _client.PutBlockAsync(
                                    _dest.AccountName!,
                                    _dest.ContainerName!,
                                    item.DestPath,
                                    blockIds[blockIndex],
                                    chunk,
                                    ct
                                ),
                            ct
                        );

                        var uploaded = Interlocked.Add(ref totalUploaded, length);
                        ReportProgress(index, Math.Min(uploaded, item.Size), item.Size);
                        _journal?.WriteBlockCompleted(item.SourcePath, blockIndex, offset, length);
                    }
                    finally
                    {
                        uploadSemaphore.Release();
                    }
                },
                ct
            );
        }

        await Task.WhenAll(blockTasks);

        await RetryAsync(
            () =>
                _client.PutBlockListAsync(
                    _dest.AccountName!,
                    _dest.ContainerName!,
                    item.DestPath,
                    blockIds,
                    ct
                ),
            ct
        );
    }

    private async Task DownloadAsync(int index, TransferItem item, CancellationToken ct)
    {
        // Ensure destination directory exists
        var dir = Path.GetDirectoryName(item.DestPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        if (item.Size <= _blockSize)
        {
            // Single-shot download
            await using var stream = await RetryAsync(
                () =>
                    _client.GetBlobAsync(
                        item.SourceAccountName!,
                        item.SourceContainerName!,
                        item.SourcePath,
                        ct
                    ),
                ct
            );
            await using var fileStream = new FileStream(
                item.DestPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true
            );
            await stream.CopyToAsync(fileStream, ct);
            ReportProgress(index, item.Size, item.Size);
            return;
        }

        // Chunked download
        var blockCount = (int)Math.Ceiling((double)item.Size / _blockSize);
        var completedBlocks = _journal?.GetCompletedBlocks(item.SourcePath) ?? new HashSet<int>();
        var downloadSemaphore = new SemaphoreSlim(_blocksPerBlob, _blocksPerBlob);
        long totalDownloaded = completedBlocks.Count * (long)_blockSize;

        // Pre-create file at full size
        await using (
            var fs = new FileStream(
                item.DestPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite
            )
        )
        {
            if (fs.Length < item.Size)
                fs.SetLength(item.Size);
        }

        if (totalDownloaded > 0)
            ReportProgress(index, Math.Min(totalDownloaded, item.Size), item.Size);

        var blockTasks = new Task[blockCount];
        for (int b = 0; b < blockCount; b++)
        {
            var blockIndex = b;
            if (completedBlocks.Contains(blockIndex))
            {
                blockTasks[blockIndex] = Task.CompletedTask;
                continue;
            }

            blockTasks[blockIndex] = Task.Run(
                async () =>
                {
                    await downloadSemaphore.WaitAsync(ct);
                    try
                    {
                        var offset = (long)blockIndex * _blockSize;
                        var length = (int)Math.Min(_blockSize, item.Size - offset);

                        await using var stream = await RetryAsync(
                            () =>
                                _client.GetBlobRangeAsync(
                                    item.SourceAccountName!,
                                    item.SourceContainerName!,
                                    item.SourcePath,
                                    offset,
                                    length,
                                    ct
                                ),
                            ct
                        );

                        var buffer = ArrayPool<byte>.Shared.Rent(length);
                        try
                        {
                            var bytesRead = 0;
                            while (bytesRead < length)
                            {
                                var read = await stream.ReadAsync(
                                    buffer.AsMemory(bytesRead, length - bytesRead),
                                    ct
                                );
                                if (read == 0)
                                    break;
                                bytesRead += read;
                            }

                            await using var fs = new FileStream(
                                item.DestPath,
                                FileMode.Open,
                                FileAccess.Write,
                                FileShare.ReadWrite
                            );
                            fs.Seek(offset, SeekOrigin.Begin);
                            await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }

                        var downloaded = Interlocked.Add(ref totalDownloaded, length);
                        ReportProgress(index, Math.Min(downloaded, item.Size), item.Size);
                        _journal?.WriteBlockCompleted(item.SourcePath, blockIndex, offset, length);
                    }
                    finally
                    {
                        downloadSemaphore.Release();
                    }
                },
                ct
            );
        }

        await Task.WhenAll(blockTasks);
    }

    private async Task VerifyAsync(int index, TransferItem item, CancellationToken ct)
    {
        _progressChannel.Writer.TryWrite(
            new TransferProgressEvent(index, 0, item.Size, TransferStatus.Verifying, null)
        );

        using var md5 = MD5.Create();
        await using var fs = new FileStream(
            item.DestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true
        );

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            md5.TransformBlock(buffer, 0, bytesRead, null, 0);
            totalRead += bytesRead;
            _progressChannel.Writer.TryWrite(
                new TransferProgressEvent(
                    index,
                    totalRead,
                    item.Size,
                    TransferStatus.Verifying,
                    null
                )
            );
        }

        md5.TransformFinalBlock([], 0, 0);
        var computedHash = Convert.ToBase64String(md5.Hash!);

        if (computedHash != item.ContentMD5)
            throw new InvalidOperationException(
                $"Hash mismatch: expected {item.ContentMD5}, computed {computedHash}"
            );
    }

    private async Task ServerSideCopyAsync(int index, TransferItem item, CancellationToken ct)
    {
        var sourceUrl =
            $"https://{item.SourceAccountName}.blob.core.windows.net/{item.SourceContainerName}/{item.SourcePath}";

        await _client.StartCopyBlobAsync(
            _dest.AccountName!,
            _dest.ContainerName!,
            item.DestPath,
            sourceUrl,
            ct
        );

        // Poll until copy completes
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            var status = await _client.GetCopyStatusAsync(
                _dest.AccountName!,
                _dest.ContainerName!,
                item.DestPath,
                ct
            );

            switch (status)
            {
                case "success":
                    ReportProgress(index, item.Size, item.Size);
                    return;
                case "failed":
                    throw new InvalidOperationException(
                        $"Server-side copy failed for '{item.SourcePath}'."
                    );
                case "aborted":
                    throw new OperationCanceledException("Server-side copy was aborted.");
                default:
                    // "pending" — keep polling
                    break;
            }
        }
    }

    private async Task ClientSideCopyAsync(int index, TransferItem item, CancellationToken ct)
    {
        // Download from source, upload to destination
        await using var stream = await _client.GetBlobAsync(
            item.SourceAccountName!,
            item.SourceContainerName!,
            item.SourcePath,
            ct
        );

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        await _client.PutBlobAsync(
            _dest.AccountName!,
            _dest.ContainerName!,
            item.DestPath,
            bytes,
            ct
        );

        ReportProgress(index, item.Size, item.Size);
    }

    private void ReportProgress(int index, long bytesTransferred, long totalBytes)
    {
        _progressChannel.Writer.TryWrite(
            new TransferProgressEvent(
                index,
                bytesTransferred,
                totalBytes,
                TransferStatus.InProgress,
                null
            )
        );
    }

    private static async Task RetryAsync(Func<Task> action, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (HttpRequestException ex) when (attempt < maxRetries && IsRetryable(ex))
            {
                var delay = TimeSpan.FromMilliseconds(
                    Math.Pow(2, attempt) * 500 + Random.Shared.Next(200)
                );
                await Task.Delay(delay, ct);
            }
        }
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (attempt < maxRetries && IsRetryable(ex))
            {
                var delay = TimeSpan.FromMilliseconds(
                    Math.Pow(2, attempt) * 500 + Random.Shared.Next(200)
                );
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsRetryable(HttpRequestException ex) =>
        ex.StatusCode
            is System.Net.HttpStatusCode.RequestTimeout
                or System.Net.HttpStatusCode.TooManyRequests
                or System.Net.HttpStatusCode.InternalServerError
                or System.Net.HttpStatusCode.BadGateway
                or System.Net.HttpStatusCode.ServiceUnavailable
                or System.Net.HttpStatusCode.GatewayTimeout;
}
