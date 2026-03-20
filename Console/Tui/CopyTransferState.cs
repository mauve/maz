using Console.Cli.Commands.Copy;

namespace Console.Tui;

/// <summary>Per-transfer state tracked by the copy TUI.</summary>
internal sealed class CopyTransferState
{
    public TransferItem Item { get; }
    public TransferStatus Status { get; set; } = TransferStatus.Queued;
    public long BytesTransferred { get; set; }
    public string? Error { get; set; }
    public CancellationTokenSource Cts { get; }

    // Speed calculation: rolling 2-second window
    private readonly Queue<(long timestamp, long bytes)> _speedSamples = new();

    public CopyTransferState(TransferItem item, CancellationTokenSource cts)
    {
        Item = item;
        Cts = cts;
    }

    /// <summary>Bytes per second over the rolling window.</summary>
    public double BytesPerSecond { get; private set; }

    /// <summary>Estimated time remaining.</summary>
    public TimeSpan? Eta
    {
        get
        {
            if (BytesPerSecond <= 0 || Status != TransferStatus.InProgress)
                return null;
            var remaining = Item.Size - BytesTransferred;
            return TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }

    /// <summary>Elapsed time since transfer started.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    public void UpdateProgress(long bytesTransferred, TransferStatus status, string? error)
    {
        if (status == TransferStatus.InProgress && !_stopwatch.IsRunning)
            _stopwatch.Start();

        BytesTransferred = bytesTransferred;
        Status = status;
        Error = error;

        if (status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
            _stopwatch.Stop();

        // Update speed samples
        var now = Environment.TickCount64;
        _speedSamples.Enqueue((now, bytesTransferred));

        // Remove samples older than 2 seconds
        while (_speedSamples.Count > 0 && now - _speedSamples.Peek().timestamp > 2000)
            _speedSamples.Dequeue();

        if (_speedSamples.Count >= 2)
        {
            var oldest = _speedSamples.Peek();
            var elapsed = (now - oldest.timestamp) / 1000.0;
            if (elapsed > 0)
                BytesPerSecond = (bytesTransferred - oldest.bytes) / elapsed;
        }
    }
}
